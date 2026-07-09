using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Satisfy;

public sealed class Config
{
    private const int _version = 1;

    public bool AutoFetchAchievements = true;
    public bool AutoShowIfIncomplete = true;
    public bool ShowDebugUI = false;
    public JobChoice CraftJobType = JobChoice.指定职业;
    public uint SelectedCraftJob = 8;
    public uint SelectedGatherJob = 16;

    private (uint rowId, string name)[]? _craftJobs;
    private (uint rowId, string name)[] CraftJobs
    {
        get
        {
            if (_craftJobs != null)
                return _craftJobs;
            var sheet = Service.LuminaGameData?.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
            if (sheet != null)
            {
                _craftJobs = [.. sheet.Where(c => c.ClassJobCategory.RowId == 33).Select(c => (c.RowId, c.Name.ToString()))];
            }
            return _craftJobs ?? [];
        }
    }

    private (uint rowId, string name)[]? _gatherJobs;
    private (uint rowId, string name)[] GatherJobs
    {
        get
        {
            if (_gatherJobs != null)
                return _gatherJobs;
            var sheet = Service.LuminaGameData?.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
            if (sheet != null)
            {
                _gatherJobs = [.. sheet.Where(c => c.RowId is 16 or 17).Select(c => (c.RowId, c.Name.ToString()))];
            }
            return _gatherJobs ?? [];
        }
    }

    public enum JobChoice
    {
        [Description("指定职业")]
        指定职业,
        [Description("当前职业")]
        当前职业,
        [Description("经验最低")]
        等级最低的职业,
        [Description("经验最高")]
        等级最高的职业,
    }

    public event Action? Modified;

    public void NotifyModified() => Modified?.Invoke();

    public void Draw()
    {
        if (ImGui.Checkbox("打开窗口时自动获取成就数据", ref AutoFetchAchievements))
            NotifyModified();
        if (ImGui.Checkbox("如果限额未用完则在登录时自动打开窗口", ref AutoShowIfIncomplete))
            NotifyModified();
        if (ImGui.Checkbox("开启 Debug", ref ShowDebugUI))
            NotifyModified();
        if (ImGuiUtils.Enum("职业选择", ref CraftJobType))
            NotifyModified();
        if (CraftJobType == JobChoice.指定职业)
        {
            var craftSelected = CraftJobs.FirstOrDefault(c => c.rowId == SelectedCraftJob);
            using (var craftCombo = ImRaii.Combo("指定制作职业", CraftJobs.Length > 0 ? craftSelected.name : "加载中..."))
            {
                if (craftCombo)
                {
                    for (var i = 0; i < CraftJobs.Length; ++i)
                    {
                        if (ImGui.Selectable(CraftJobs[i].name, SelectedCraftJob == CraftJobs[i].rowId))
                        {
                            SelectedCraftJob = CraftJobs[i].rowId;
                            NotifyModified();
                        }
                    }
                }
            }

            var gatherSelected = GatherJobs.FirstOrDefault(c => c.rowId == SelectedGatherJob);
            using var gatherCombo = ImRaii.Combo("指定采集职业", GatherJobs.Length > 0 ? gatherSelected.name : "加载中...");
            if (gatherCombo)
            {
                for (var i = 0; i < GatherJobs.Length; ++i)
                {
                    if (ImGui.Selectable(GatherJobs[i].name, SelectedGatherJob == GatherJobs[i].rowId))
                    {
                        SelectedGatherJob = GatherJobs[i].rowId;
                        NotifyModified();
                    }
                }
            }
        }
    }

    public void Load(FileInfo file)
    {
        if (!file.Exists)
        {
            Service.Log.Info($"Config file {file.FullName} does not exist, skipping load.");
            return;
        }
        try
        {
            using var json = ReadConvertFile(file);
            var ser = BuildSerializationOptions();
            var thisType = GetType();
            foreach (var jfield in json.RootElement.GetProperty("Payload").EnumerateObject())
            {
                var thisField = thisType.GetField(jfield.Name);
                if (thisField != null)
                {
                    var value = jfield.Value.Deserialize(thisField.FieldType, ser);
                    if (value != null)
                    {
                        thisField.SetValue(this, value);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Service.Log.Error($"Failed to load config from {file.FullName}: {e}");
        }
    }

    public void Save(FileInfo file)
    {
        try
        {
            WriteFile(file, jwriter =>
            {
                var ser = BuildSerializationOptions();
                JsonSerializer.Serialize(jwriter, this, GetType(), ser);
            });
        }
        catch (Exception e)
        {
            Service.Log.Error($"Failed to save config to {file.FullName}: {e}");
        }
    }

    private static JsonSerializerOptions BuildSerializationOptions() => new()
    {
        IncludeFields = true,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    private static JsonDocument ReadJson(string path)
    {
        using var fstream = File.OpenRead(path);
        return JsonDocument.Parse(fstream, new JsonDocumentOptions() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    }

    private static Utf8JsonWriter WriteJson(Stream fstream, bool indented = true) => new(fstream, new JsonWriterOptions() { Indented = indented });

    private JsonDocument ReadConvertFile(FileInfo file)
    {
        var json = ReadJson(file.FullName);
        var version = json.RootElement.TryGetProperty("Version", out var jver) ? jver.GetInt32() : 0;
        if (version > _version)
            throw new ArgumentException($"Config file version {version} is newer than supported {_version}");
        if (version == _version)
            return json;

        var converted = ConvertConfig(JsonObject.Create(json.RootElement.GetProperty("Payload"))!, version, file.Directory!);

        var original = new FileInfo(file.FullName);
        var backup = new FileInfo(file.FullName + $".v{version}");
        if (!backup.Exists)
            file.MoveTo(backup.FullName);
        WriteFile(original, jwriter => converted.WriteTo(jwriter));
        json.Dispose();

        return ReadJson(original.FullName);
    }

    private void WriteFile(FileInfo file, Action<Utf8JsonWriter> writePayload)
    {
        using var fstream = new FileStream(file.FullName, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var jwriter = WriteJson(fstream);
        jwriter.WriteStartObject();
        jwriter.WriteNumber("Version", _version);
        jwriter.WritePropertyName("Payload");
        writePayload(jwriter);
        jwriter.WriteEndObject();
    }

    private static JsonObject ConvertConfig(JsonObject payload, int version, DirectoryInfo dir)
    {
        return payload;
    }
}
