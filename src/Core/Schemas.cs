using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RhinoMCP
{
    /// <summary>
    /// Shared JSON Schema fragments. Tools just write {"$ref":"#/$defs/target"}; the registry injects the
    /// definitions into the tool's inputSchema. Tools may also concatenate the fragments directly.
    /// </summary>
    public static class Schemas
    {
        public const string Point = @"{""type"":""array"",""items"":{""type"":""number""},""minItems"":3,""maxItems"":3,""description"":""[x,y,z] in document units""}";
        public const string Plane = @"{""oneOf"":[{""type"":""string"",""description"":""world_xy | world_yz | world_zx | named CPlane""},{""type"":""object"",""properties"":{""origin"":{""$ref"":""#/$defs/point""},""x_axis"":{""$ref"":""#/$defs/point""},""y_axis"":{""$ref"":""#/$defs/point""}},""required"":[""origin"",""x_axis"",""y_axis""]}]}";
        public const string Target = @"{""description"":""Object handle (o12), GUID, {set:name}, {query:{layer,type,name}} or an array of those"",""oneOf"":[{""type"":""string""},{""type"":""object"",""properties"":{""set"":{""type"":""string""}},""required"":[""set""]},{""type"":""object"",""properties"":{""query"":{""type"":""object"",""properties"":{""layer"":{""type"":""string""},""type"":{""type"":""string""},""name"":{""type"":""string"",""description"":""wildcards * ?""}}}},""required"":[""query""]},{""type"":""array"",""items"":{}}]}";

        /// <summary>Wraps property fragments in an object schema: Schemas.Object("\"a\":{...}", "\"a\"").</summary>
        public static string Object(string properties, string required = "")
        {
            return "{\"type\":\"object\",\"properties\":{" + properties + "}" +
                   (required.Length > 0 ? ",\"required\":[" + required + "]" : "") + ",\"additionalProperties\":false}";
        }

        /// <summary>Parses a tool schema and injects $defs when it contains $ref's to the shared definitions.</summary>
        public static JObject Parse(string schema)
        {
            var o = JObject.Parse(schema);
            if (o["$defs"] == null && schema.Contains("#/$defs/"))
            {
                var defs = new JObject();
                defs["point"] = JObject.Parse(Point);
                defs["plane"] = JObject.Parse(Plane);
                defs["target"] = JObject.Parse(Target);
                o["$defs"] = defs;
            }
            return o;
        }
    }
}
