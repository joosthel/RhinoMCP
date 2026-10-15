// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "rhinomcp-bridge",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(name: "rhinomcp-bridge", path: "Sources/rhinomcp-bridge")
    ]
)
