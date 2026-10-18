using System;
using System.Globalization;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.PSTools.Mapping
{
    /// <summary>
    /// Maps serialized PowerShell objects to DTOs by property name. Missing properties map to null (different module or
    /// VMS versions expose different properties); nothing is parsed from formatted text.
    /// </summary>
    public static class DtoMapper
    {
        public static string Str(JsonObject o, string name) => o?[name] is JsonNode n && !n.IsNull ? (n.AsString() ?? n.ToJson()) : null;

        public static bool? Bool(JsonObject o, string name) => o?[name]?.AsBool();

        public static int? Int(JsonObject o, string name)
        {
            var v = o?[name]?.AsInt64();
            return v.HasValue && v.Value >= int.MinValue && v.Value <= int.MaxValue ? (int?)v.Value : null;
        }

        public static long? Long(JsonObject o, string name) => o?[name]?.AsInt64();

        public static double? Dbl(JsonObject o, string name) => o?[name]?.AsDouble();

        public static Guid Guid(JsonObject o, string name) => System.Guid.TryParse(Str(o, name), out var g) ? g : System.Guid.Empty;

        public static DateTime? Date(JsonObject o, string name) =>
            DateTime.TryParse(Str(o, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : (DateTime?)null;

        private static T Base<T>(T obj, JsonObject o, string objectType) where T : VmsObject
        {
            obj.Handle = Str(o, "$handle");
            obj.ObjectType = objectType;
            obj.Id = Guid(o, "Id");
            obj.Path = Str(o, "Path");
            obj.Name = Str(o, "Name");
            return obj;
        }

        public static ManagementServerInfo ManagementServer(JsonObject o) => Base(new ManagementServerInfo
        {
            ComputerName = Str(o, "ComputerName"),
            Version = Str(o, "Version"),
            Description = Str(o, "Description")
        }, o, "ManagementServer");

        public static RecordingServerInfo RecordingServer(JsonObject o) => Base(new RecordingServerInfo
        {
            HostName = Str(o, "HostName"),
            WebServerUri = Str(o, "WebServerUri"),
            ActiveWebServerUri = Str(o, "ActiveWebServerUri"),
            Enabled = Bool(o, "Enabled"),
            Description = Str(o, "Description"),
            PublicAccessEnabled = Bool(o, "PublicAccessEnabled"),
            LastModified = Date(o, "LastModified")
        }, o, "RecordingServer");

        public static HardwareInfo Hardware(JsonObject o) => Base(new HardwareInfo
        {
            Address = Str(o, "Address"),
            Model = Str(o, "Model"),
            Enabled = Bool(o, "Enabled"),
            UserName = Str(o, "UserName"),
            Description = Str(o, "Description"),
            HardwareDriverPath = Str(o, "HardwareDriverPath"),
            ParentItemPath = Str(o, "ParentItemPath"),
            LastModified = Date(o, "LastModified")
        }, o, "Hardware");

        public static CameraInfo Camera(JsonObject o) => Base(new CameraInfo
        {
            ShortName = Str(o, "ShortName"),
            Description = Str(o, "Description"),
            Enabled = Bool(o, "Enabled"),
            Channel = Int(o, "Channel"),
            ParentItemPath = Str(o, "ParentItemPath"),
            RecordingEnabled = Bool(o, "RecordingEnabled"),
            RecordingFramerate = Dbl(o, "RecordingFramerate"),
            RecordKeyframesOnly = Bool(o, "RecordKeyframesOnly"),
            PrebufferEnabled = Bool(o, "PrebufferEnabled"),
            PrebufferSeconds = Int(o, "PrebufferSeconds"),
            RecordingStorage = Str(o, "RecordingStorage"),
            GisPoint = Str(o, "GisPoint"),
            LastModified = Date(o, "LastModified")
        }, o, "Camera");

        public static StorageInfo Storage(JsonObject o) => Base(new StorageInfo
        {
            DiskPath = Str(o, "DiskPath"),
            RetainMinutes = Long(o, "RetainMinutes"),
            MaxSize = Long(o, "MaxSize"),
            IsDefault = Bool(o, "IsDefault"),
            ParentItemPath = Str(o, "ParentItemPath"),
            Description = Str(o, "Description")
        }, o, "Storage");

        public static HardwareDriverInfo HardwareDriver(JsonObject o) => Base(new HardwareDriverInfo
        {
            Number = Int(o, "Number"),
            GroupName = Str(o, "GroupName"),
            DriverType = Str(o, "DriverType")
        }, o, "HardwareDriver");

        public static HardwareScanResultInfo HardwareScanResult(JsonObject o)
        {
            var r = new HardwareScanResultInfo
            {
                Handle = Str(o, "$handle"),
                ObjectType = "VmsHardwareScanResult",
                Path = Str(o, "Path"),
                HardwareAddress = Str(o, "HardwareAddress"),
                UserName = Str(o, "UserName"),
                MacAddress = Str(o, "MacAddress"),
                HardwareDriverPath = Str(o, "HardwareDriverPath"),
                HardwareScanValidated = Bool(o, "HardwareScanValidated") ?? false,
                MacAddressExistsLocal = Bool(o, "MacAddressExistsLocal") ?? false,
                MacAddressExistsGlobal = Bool(o, "MacAddressExistsGlobal") ?? false,
                State = Str(o, "State"),
                ErrorText = Str(o, "ErrorText")
            };
            r.Name = r.HardwareAddress;
            return r;
        }

        public static DeviceStatusInfo DeviceStatus(JsonObject o) => new DeviceStatusInfo
        {
            DeviceName = Str(o, "DeviceName"),
            DeviceType = Str(o, "DeviceType"),
            RecorderName = Str(o, "RecorderName"),
            RecorderId = Guid(o, "RecorderId"),
            DeviceId = Guid(o, "DeviceId"),
            Enabled = Bool(o, "Enabled"),
            Started = Bool(o, "Started"),
            Recording = Bool(o, "Recording"),
            Motion = Bool(o, "Motion"),
            Error = Bool(o, "Error"),
            ErrorNoConnection = Bool(o, "ErrorNoConnection"),
            ErrorNotLicensed = Bool(o, "ErrorNotLicensed"),
            ErrorOverflow = Bool(o, "ErrorOverflow"),
            ErrorWritingGop = Bool(o, "ErrorWritingGop"),
            DbMoveInProgress = Bool(o, "DbMoveInProgress"),
            DbRepairInProgress = Bool(o, "DbRepairInProgress"),
            Time = Date(o, "Time")
        };

        public static RoleInfo Role(JsonObject o) => Base(new RoleInfo { RoleType = Str(o, "RoleType"), Description = Str(o, "Description") }, o, "Role");

        public static GenericItem Generic(JsonObject o, string objectType)
        {
            var g = Base(new GenericItem(), o, objectType);
            if (g.Name == null) g.Name = Str(o, "DisplayName");
            foreach (var kv in o)
            {
                if (kv.Key.StartsWith("$", StringComparison.Ordinal)) continue;
                g.Properties[kv.Key] = ToClr(kv.Value);
            }

            return g;
        }

        public static object ToClr(JsonNode n)
        {
            switch (n?.Kind ?? JsonKind.Null)
            {
                case JsonKind.Null: return null;
                case JsonKind.Bool: return n.AsBool();
                case JsonKind.Number:
                {
                    var l = n.AsInt64();
                    if (l.HasValue && ((JsonNumber)n).Raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0) return l.Value;
                    return n.AsDouble();
                }

                case JsonKind.String: return n.AsString();
                case JsonKind.Object: return n.AsObject().GetString("$string") ?? n.AsObject().GetString("Name") ?? n.AsObject().GetString("$type");
                default: return n.ToJson();
            }
        }
    }
}
