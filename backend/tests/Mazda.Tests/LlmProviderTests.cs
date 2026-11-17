using Mazda.Application.Modules.Settings;
using Mazda.Domain.Modules.Settings;
using Mazda.Infrastructure.Llm;
using OpenAI.Chat;
using Xunit;

namespace Mazda.Tests;

/// <summary>Settings precedence (saved over .env) and the Claude Code bridge request.</summary>
public class LlmProviderTests
{
    private static readonly LlmEnvironment Env = new(
        Provider: "openai",
        OpenAiBaseUrl: "https://api.example.test/v1",
        OpenAiApiKey: "env-key",
        OpenAiAnalyzerModel: "env-small",
        OpenAiGeneratorModel: "env-big",
        ClaudeBridgeUrl: "",
        ClaudeBridgeToken: "tok",
        ClaudeAnalyzerModel: "",
        ClaudeGeneratorModel: "",
        TimeoutSeconds: 600);

    [Fact]
    public void Nothing_saved_means_env_and_built_in_defaults()
    {
        var r = LlmSettingsResolver.Resolve(null, Env);
        Assert.Equal(LlmProviders.OpenAi, r.Provider);
        Assert.Equal("env-key", r.OpenAiApiKey);
        Assert.Equal("env-small", r.AnalyzerModel);
        Assert.Equal(LlmSettingsResolver.DefaultBridgeUrl, r.ClaudeBridgeUrl);
        Assert.Equal("sonnet", r.ClaudeAnalyzerModel);
        Assert.Equal("opus", r.ClaudeGeneratorModel);
        Assert.Equal(600, r.TimeoutSeconds);
    }

    [Fact]
    public void Saved_values_win_and_choose_the_provider_models()
    {
        var s = LlmSettings.CreateDefault();
        s.Update(LlmProviders.ClaudeCode, "", null, false, "", "", "", "haiku", "", 120);
        var r = LlmSettingsResolver.Resolve(s, Env);
        Assert.Equal(LlmProviders.ClaudeCode, r.Provider);
        Assert.Equal("haiku", r.AnalyzerModel);
        Assert.Equal("opus", r.GeneratorModel);
        Assert.Equal(120, r.TimeoutSeconds);
        Assert.Equal("tok", r.ClaudeBridgeToken);
    }

    [Fact]
    public void Api_key_is_kept_unless_replaced_or_cleared()
    {
        var s = LlmSettings.CreateDefault();
        s.Update(LlmProviders.OpenAi, "", "saved-key", false, "", "", "", "", "", 0);
        Assert.Equal("saved-key", LlmSettingsResolver.Resolve(s, Env).OpenAiApiKey);

        s.Update(LlmProviders.OpenAi, "", null, false, "", "", "", "", "", 0);   // the UI never sends it back
        Assert.Equal("saved-key", LlmSettingsResolver.Resolve(s, Env).OpenAiApiKey);

        s.Update(LlmProviders.OpenAi, "", "", true, "", "", "", "", "", 0);      // clear: back to .env
        Assert.Equal("env-key", LlmSettingsResolver.Resolve(s, Env).OpenAiApiKey);
    }

    [Fact]
    public void Bridge_request_has_system_and_one_user_message()
    {
        var r = ClaudeBridgeRequest.Build(new List<ChatMessage>
        {
            new SystemChatMessage("rules"),
            new UserChatMessage("do it"),
        }, "sonnet", 300);
        Assert.Equal("rules", r.System);
        Assert.Equal("sonnet", r.Model);
        Assert.Equal(300, r.TimeoutSec);
        var p = Assert.Single(r.Content);
        Assert.Equal("text", p.Type);
        Assert.Equal("do it", p.Text);
    }

    [Fact]
    public void Bridge_request_keeps_pictures_in_order()
    {
        var jpeg = BinaryData.FromBytes(new byte[] { 1, 2, 3 });
        var r = ClaudeBridgeRequest.Build(new List<ChatMessage>
        {
            new SystemChatMessage("judge"),
            new UserChatMessage(new List<ChatMessageContentPart>
            {
                ChatMessageContentPart.CreateTextPart("before:"),
                ChatMessageContentPart.CreateImagePart(jpeg, "image/jpeg"),
                ChatMessageContentPart.CreateTextPart("after:"),
                ChatMessageContentPart.CreateImagePart(jpeg, "image/jpeg"),
            }),
        }, "opus", 60);
        Assert.Equal(new[] { "text", "image", "text", "image" }, r.Content.Select(c => c.Type));
        Assert.Equal("AQID", r.Content[1].Data);
        Assert.Equal("image/jpeg", r.Content[1].MediaType);
    }

    [Fact]
    public void Bridge_request_flattens_a_repair_round()
    {
        var r = ClaudeBridgeRequest.Build(new List<ChatMessage>
        {
            new SystemChatMessage("author"),
            new UserChatMessage("write the catalogue"),
            new AssistantChatMessage("{\"functions\":[]}"),
            new UserChatMessage("fix function X"),
        }, "opus", 60);
        var p = Assert.Single(r.Content);
        var t = p.Text!;
        Assert.True(t.IndexOf("write the catalogue", StringComparison.Ordinal) < t.IndexOf("<your_previous_answer>", StringComparison.Ordinal));
        Assert.Contains("{\"functions\":[]}", t);
        Assert.True(t.IndexOf("</your_previous_answer>", StringComparison.Ordinal) < t.IndexOf("fix function X", StringComparison.Ordinal));
        Assert.Contains("<follow_up>", t);
    }
}
