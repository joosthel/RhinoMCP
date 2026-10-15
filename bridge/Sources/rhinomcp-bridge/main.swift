// rhinomcp-bridge: stdio <-> HTTP bridge for the RhinoMCP plugin.
// stdin: newline-delimited JSON-RPC. stdout: one JSON line per response (nothing else). stderr: logs.
import Foundation

let bridgeVersion = "0.2.0"
let supportedProtocols = ["2025-11-25", "2025-06-18", "2025-03-26"]
let dataDir: String = {
    if let override = ProcessInfo.processInfo.environment["RHINOMCP_DATA_DIR"], !override.isEmpty { return override }
    return NSHomeDirectory() + "/Library/Application Support/RhinoMCP"
}()

func log(_ s: String) {
    FileHandle.standardError.write(("[rhinomcp-bridge] " + s + "\n").data(using: .utf8)!)
}

func emit(_ obj: [String: Any]) {
    guard let data = try? JSONSerialization.data(withJSONObject: obj, options: [.withoutEscapingSlashes]) else { return }
    emitRaw(data)
}

func emitRaw(_ data: Data) {
    var out = data
    // JSON strings never contain raw newlines, so any present are inter-token whitespace.
    if out.contains(10) || out.contains(13) {
        let s = String(decoding: out, as: UTF8.self)
            .replacingOccurrences(of: "\r", with: " ").replacingOccurrences(of: "\n", with: " ")
        out = Data(s.utf8)
    }
    out.append(10)
    FileHandle.standardOutput.write(out)
}

// MARK: discovery

struct Instance { let pid: Int32; let port: Int; let mtime: Date }

func pidAlive(_ pid: Int32) -> Bool {
    if pid <= 0 { return false }
    if kill(pid, 0) == 0 { return true }
    return errno == EPERM
}

func discoverInstance() -> Instance? {
    let dir = dataDir + "/instances"
    guard let names = try? FileManager.default.contentsOfDirectory(atPath: dir) else { return nil }
    var found: [Instance] = []
    for n in names where n.hasSuffix(".json") {
        let path = dir + "/" + n
        guard let d = FileManager.default.contents(atPath: path),
              let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any],
              let pidN = j["pid"] as? NSNumber, let portN = j["port"] as? NSNumber else { continue }
        let pid = pidN.int32Value
        guard pidAlive(pid) else { continue }
        let m = (try? FileManager.default.attributesOfItem(atPath: path)[.modificationDate] as? Date) ?? Date.distantPast
        found.append(Instance(pid: pid, port: portN.intValue, mtime: m))
    }
    return found.sorted { $0.mtime > $1.mtime }.first
}

func readToken() -> String? {
    guard let d = FileManager.default.contents(atPath: dataDir + "/config.json"),
          let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any],
          let t = j["token"] as? String, !t.isEmpty else { return nil }
    return t
}

// MARK: HTTP

enum PostResult {
    case ok(Data)          // 200
    case accepted          // 202
    case unauthorized      // 401
    case httpError(Int, Data)
    case connectionFailed(String)
}

final class Box { var result: PostResult = .connectionFailed("no response") }

let session: URLSession = {
    let c = URLSessionConfiguration.ephemeral
    c.timeoutIntervalForRequest = 600
    c.timeoutIntervalForResource = 600
    c.connectionProxyDictionary = [:] // never route loopback through a proxy
    c.httpShouldSetCookies = false
    return URLSession(configuration: c)
}()

func post(port: Int, token: String, body: Data) -> PostResult {
    var req = URLRequest(url: URL(string: "http://127.0.0.1:\(port)/mcp")!)
    req.httpMethod = "POST"
    req.httpBody = body
    req.setValue("application/json", forHTTPHeaderField: "Content-Type")
    req.setValue("application/json, text/event-stream", forHTTPHeaderField: "Accept")
    req.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
    let box = Box()
    let sem = DispatchSemaphore(value: 0)
    session.dataTask(with: req) { data, resp, err in
        if let err = err { box.result = .connectionFailed(err.localizedDescription) }
        else if let h = resp as? HTTPURLResponse {
            let d = data ?? Data()
            switch h.statusCode {
            case 200: box.result = .ok(d)
            case 202: box.result = .accepted
            case 401: box.result = .unauthorized
            default: box.result = .httpError(h.statusCode, d)
            }
        }
        sem.signal()
    }.resume()
    sem.wait()
    return box.result
}

// MARK: fallback (no Rhino)

let statusText = "Rhino is not reachable. Open Rhino 7 with the RhinoMCP plugin installed (run scripts/install.sh or install the Yak package, then restart Rhino once). Once Rhino is running, call this tool again or retry your request; no restart of this client is needed."

func fallbackResponse(id: Any, method: String, params: [String: Any]) -> [String: Any] {
    func result(_ r: [String: Any]) -> [String: Any] { ["jsonrpc": "2.0", "id": id, "result": r] }
    switch method {
    case "initialize":
        let wanted = params["protocolVersion"] as? String ?? ""
        let v = supportedProtocols.contains(wanted) ? wanted : "2025-06-18"
        return result([
            "protocolVersion": v,
            "capabilities": ["tools": ["listChanged": true]],
            "serverInfo": ["name": "rhino-mcp", "version": bridgeVersion + " (bridge, Rhino not running)"],
            "instructions": statusText,
        ])
    case "ping":
        return result([:])
    case "tools/list":
        let schema: [String: Any] = ["type": "object", "properties": [String: Any](), "additionalProperties": false]
        let tool: [String: Any] = [
            "name": "rhino_status",
            "description": "Rhino is not reachable right now. Call this to see how to connect: open Rhino 7 with the RhinoMCP plugin. Re-checks on every call.",
            "inputSchema": schema,
            "annotations": ["readOnlyHint": true],
        ]
        return result(["tools": [tool]])
    case "tools/call":
        let name = params["name"] as? String ?? ""
        if name == "rhino_status" {
            return result(["content": [["type": "text", "text": statusText]], "isError": false])
        }
        return result(["content": [["type": "text", "text": "Tool '\(name)' is unavailable because Rhino is not running. " + statusText]], "isError": true])
    case "resources/list": return result(["resources": [Any]()])
    case "prompts/list": return result(["prompts": [Any]()])
    default:
        return jsonRpcError(id: id, code: -32601, message: "Method not found (Rhino is not running): \(method)")
    }
}

func jsonRpcError(id: Any, code: Int, message: String) -> [String: Any] {
    ["jsonrpc": "2.0", "id": id, "error": ["code": code, "message": message]]
}

// MARK: main loop

var current: Instance? = nil
var live: Bool? = nil           // last observed mode, nil until first request
var initialized = false

func setMode(_ isLive: Bool) {
    if let prev = live, prev != isLive, initialized {
        emit(["jsonrpc": "2.0", "method": "notifications/tools/list_changed"])
    }
    live = isLive
}

/// Returns nil when no Rhino is reachable; otherwise the final result.
func forward(_ body: Data) -> PostResult? {
    var attempts = 0
    var lastFailure = ""
    while attempts < 2 {
        if current == nil { current = discoverInstance() }
        guard let inst = current else { return nil }
        guard let token = readToken() else { log("no token in config.json"); return nil }
        let r = post(port: inst.port, token: token, body: body)
        switch r {
        case .connectionFailed(let why):
            lastFailure = why
            log("connection to port \(inst.port) failed (\(why)); re-discovering")
            current = nil
            attempts += 1
        case .unauthorized:
            log("401 from port \(inst.port); re-discovering")
            lastFailure = "unauthorized"
            current = nil
            attempts += 1
        default:
            return r
        }
    }
    log("giving up: \(lastFailure)")
    return nil
}

while let line = readLine(strippingNewline: true) {
    let trimmed = line.trimmingCharacters(in: .whitespaces)
    if trimmed.isEmpty { continue }
    let body = Data(trimmed.utf8)
    guard let parsed = try? JSONSerialization.jsonObject(with: body) else {
        emit(jsonRpcError(id: NSNull(), code: -32700, message: "Parse error"))
        continue
    }
    let msg = parsed as? [String: Any]
    let method = msg?["method"] as? String ?? ""
    let id: Any? = msg?["id"]   // nil => notification (or a response from the client)
    let params = msg?["params"] as? [String: Any] ?? [:]
    if method == "notifications/initialized" { initialized = true }
    if method == "initialize" { initialized = false }

    if let r = forward(body) {
        switch r {
        case .ok(let d):
            setMode(true)
            emitRaw(d)
        case .accepted:
            setMode(true)
        case .httpError(let code, let d):
            setMode(true)
            if let id = id {
                emit(jsonRpcError(id: id, code: -32603, message: "Rhino returned HTTP \(code): \(String(decoding: d, as: UTF8.self))"))
            }
        default: break
        }
        continue
    }

    // Rhino unreachable: answer ourselves.
    setMode(false)
    guard let idv = id else { continue } // notifications get no output
    emit(fallbackResponse(id: idv, method: method, params: params))
}
