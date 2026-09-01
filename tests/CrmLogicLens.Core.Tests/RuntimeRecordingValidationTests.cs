using System.Reflection;
using CrmLogicLens.Api.Services;
using CrmLogicLens.Core;

namespace CrmLogicLens.Core.Tests;

public sealed class RuntimeRecordingValidationTests
{
    [Fact]
    public void EnhancedFormEvents_AreAcceptedByChatValidation()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var events = new RuntimeRecordingEvent[]
        {
            new(1, "form-inspection", "字段状态检查", "{\"field\":\"new_status\"}", capturedAt),
            new(2, "form-field-change", "字段 new_status 已改变", "当前值尚未保存", capturedAt.AddSeconds(1)),
            new(3, "form-save", "窗体开始保存", "保存模式：1", capturedAt.AddSeconds(2)),
            new(4, "form-data-load", "窗体数据已重新加载", null, capturedAt.AddSeconds(3))
        };

        var validation = typeof(EvidenceQueryService).GetMethod(
            "ValidateRuntimeRecording",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(validation);
        var exception = Record.Exception(() => validation!.Invoke(null, [true, events]));
        Assert.Null(exception);
    }
}
