using OpenPin.Translate;

namespace OpenPin.Tests.Translate;

public class OpenAiModelListTests
{
    [Theory]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1/models")]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1/models")]
    [InlineData("http://localhost:11434/v1/chat/completions", "http://localhost:11434/v1/models")]
    [InlineData("dashscope.aliyuncs.com/compatible-mode/v1", "https://dashscope.aliyuncs.com/compatible-mode/v1/models")]
    public void ModelsEndpoint(string baseUrl, string expected) =>
        Assert.Equal(expected, OpenAiModelList.ModelsEndpoint(baseUrl).ToString());

    [Fact]
    public void ParsesOpenAiListSortedAndDistinct()
    {
        var ids = OpenAiModelList.Parse("""{"object":"list","data":[{"id":"deepseek-reasoner","object":"model"},{"id":"deepseek-chat"},{"id":"deepseek-chat"}]}""");
        Assert.Equal(new[] { "deepseek-chat", "deepseek-reasoner" }, ids);
    }

    [Fact]
    public void ParsesAlternativeShapes()
    {
        Assert.Equal(new[] { "a", "b" }, OpenAiModelList.Parse("""["b","a"]"""));
        Assert.Equal(new[] { "qwen2.5:7b" }, OpenAiModelList.Parse("""{"models":[{"name":"qwen2.5:7b"}]}"""));
        Assert.Empty(OpenAiModelList.Parse("""{"error":"nope"}"""));
    }

    [Fact]
    public async Task FetchSendsBearerAndParses()
    {
        string? auth = null, url = null;
        var handler = new StubHandler(req =>
        {
            auth = req.Headers.Authorization?.ToString();
            url = req.RequestUri!.ToString();
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("""{"data":[{"id":"m1"}]}"""),
            };
        });
        using var http = new System.Net.Http.HttpClient(handler);
        var ids = await OpenAiModelList.FetchAsync(http, "https://example.test/v1", "sk-123", CancellationToken.None);
        Assert.Equal(new[] { "m1" }, ids);
        Assert.Equal("Bearer sk-123", auth);
        Assert.Equal("https://example.test/v1/models", url);
    }

    [Fact]
    public void FiltersNonChatAndPrioritizesChatModels()
    {
        var raw = new[] { "text-embedding-3-small", "whisper-1", "bge-m3", "deepseek-chat", "gpt-4o-mini", "custom-model" };
        var sorted = OpenAiModelList.SortAndFilter(raw);
        // Non-chat models filtered out; preferred chat models come first
        Assert.Equal(new[] { "deepseek-chat", "gpt-4o-mini", "custom-model" }, sorted);
    }

    [Fact]
    public void PreservesAllIfEverythingWasFiltered()
    {
        var raw = new[] { "text-embedding-3-small", "bge-m3" };
        var sorted = OpenAiModelList.SortAndFilter(raw);
        Assert.Equal(new[] { "bge-m3", "text-embedding-3-small" }, sorted);
    }

    [Fact]
    public void PrioritizesFastChatOverReasoningModels()
    {
        var raw = new[] { "deepseek-ai/DeepSeek-R1", "deepseek-ai/DeepSeek-V3", "qwen/qwq-32b", "deepseek-chat" };
        var sorted = OpenAiModelList.SortAndFilter(raw);
        Assert.Equal("deepseek-ai/DeepSeek-V3", sorted[0]);
        Assert.Equal("deepseek-chat", sorted[1]);
        Assert.True(OpenAiModelList.IsReasoningModel("deepseek-ai/DeepSeek-R1"));
        Assert.True(OpenAiModelList.IsReasoningModel("deepseek-reasoner"));
        Assert.False(OpenAiModelList.IsReasoningModel("deepseek-chat"));
        Assert.False(OpenAiModelList.IsReasoningModel("deepseek-ai/DeepSeek-V3"));
    }

    [Fact]
    public async Task FetchOllamaFallsBackToApiTagsOn404()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().EndsWith("/models"))
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            if (req.RequestUri!.ToString().EndsWith("/api/tags"))
            {
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent("""{"models":[{"name":"deepseek-r1:8b"}]}"""),
                };
            }
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        using var http = new System.Net.Http.HttpClient(handler);
        var models = await OpenAiModelList.FetchAsync(http, "http://localhost:11434/v1", null, CancellationToken.None);
        Assert.Equal(new[] { "deepseek-r1:8b" }, models);
    }

    [Fact]
    public async Task FetchWithoutKeySendsNoAuthorization()
    {
        bool hadAuth = true;
        var handler = new StubHandler(req =>
        {
            hadAuth = req.Headers.Authorization != null;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("""{"data":[]}""") };
        });
        using var http = new System.Net.Http.HttpClient(handler);
        await OpenAiModelList.FetchAsync(http, "http://localhost:11434/v1", null, CancellationToken.None);
        Assert.False(hadAuth);
    }

    private sealed class StubHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> respond) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}