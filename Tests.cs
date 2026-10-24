using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LingoFloat
{
    public static class Tests
    {
        private static int passed;
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            passed++;
            Console.WriteLine("PASS " + message);
        }
        private static Settings Config(string url, string model)
        {
            return new Settings { BaseUrl = url, Model = model, ProtectedKey = Secrets.Protect("local-test-key"), TokenParameter = "max_completion_tokens" };
        }
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--seed")
                {
                    new LocalStore(args[1]).SaveSettings(Config(args[2], "mock-translator"));
                    Console.WriteLine("Isolated UI test settings created.");
                    return 0;
                }
                Run(args[0], args[1]).GetAwaiter().GetResult();
                Console.WriteLine("ALL " + passed + " CHECKS PASSED");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex.ToString()); return 1; }
        }
        private static async Task Run(string url, string root)
        {
            Check(TranslationRules.DetectMode("reading", "auto") == "word", "single word detection");
            Check(TranslationRules.DetectMode("well-known", "auto") == "word", "hyphenated word detection");
            Check(TranslationRules.DetectMode("don't", "auto") == "word", "apostrophe detection");
            Check(TranslationRules.DetectMode("café", "auto") == "word", "accented word detection");
            Check(TranslationRules.DetectMode("in spite of", "auto") == "translate", "phrase detection");
            Check(TranslationRules.DetectMode("The model learns from data.", "auto") == "translate", "sentence detection");
            Check(TranslationRules.DetectMode("你好", "auto") == "translate", "CJK does not invent IPA");
            Check(TranslationRules.DetectMode("learning", "translate") == "translate", "manual mode override");
            Check(TranslationRules.Normalize("  a\r\nb\r  ") == "a\nb", "normalize paragraph line endings");
            var s = Config(url, "stream-chat");
            Check(ApiClient.Endpoint(s).AbsolutePath == "/v1/chat/completions", "base endpoint resolution");
            s.BaseUrl = url + "/chat/completions";
            Check(ApiClient.Endpoint(s).AbsolutePath == "/v1/chat/completions", "full endpoint is not doubled");
            s.Protocol = "responses";
            Check(ApiClient.Endpoint(s).AbsolutePath == "/v1/responses", "protocol switching rewrites endpoint");
            s.BaseUrl = "https://api.openai.com";
            Check(ApiClient.Endpoint(s).AbsolutePath == "/v1/responses", "OpenAI root version resolution");
            foreach (var bad in new[] { "http://example.com/v1", "https://user:pass@example.com/v1", "https://example.com/v1?key=secret", "file:///C:/test" })
            {
                var rejected = false;
                try { s.BaseUrl = bad; ApiClient.Endpoint(s); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "reject credential-leaking/invalid endpoint");
            }
            s = Config(url, "stream-chat");
            var p = ApiClient.Payload(s, "hello", "word");
            Check(Json.Items(p["messages"]).Count() == 2 && !p.ContainsKey("previous_response_id"), "one instruction and current text only");
            Check(Convert.ToInt32(p["max_completion_tokens"]) == 512, "dictionary output cap");
            s.IncludeUsage = false;
            s.TokenParameter = "max_tokens";
            p = ApiClient.Payload(s, "hello world", "translate");
            Check(!p.ContainsKey("stream_options") && p.ContainsKey("max_tokens"), "compatible provider options");
            s.Protocol = "responses";
            p = ApiClient.Payload(s, "hello world", "translate");
            Check((bool)p["store"] == false && p.ContainsKey("instructions") && !p.ContainsKey("messages"), "Responses payload disables server storage");
            var secret = "FAKE-SECRET-FOR-TEST-ONLY";
            Check(Secrets.Unprotect(Secrets.Protect(secret)) == secret, "DPAPI roundtrip");
            var store = new LocalStore(Path.Combine(root, "store"));
            s = Config(url, "stream-chat");
            s.ProtectedKey = Secrets.Protect(secret);
            store.SaveSettings(s);
            Check(!File.ReadAllText(Path.Combine(store.Folder, "settings.json")).Contains(secret), "no plaintext key on disk");
            var ck = TranslationRules.CacheKey(s, "hello", "word");
            store.PutCache(ck, "加密缓存正文");
            Check(!File.ReadAllText(Path.Combine(store.Folder, "cache.dat")).Contains("加密缓存正文"), "cached content encrypted at rest");
            var restored = new LocalStore(store.Folder);
            Check(restored.GetCache(ck) == "加密缓存正文" && Secrets.Unprotect(restored.Settings.ProtectedKey) == secret, "settings and cache survive restart");
            var other = s.Copy(); other.Model = "different-model";
            Check(TranslationRules.CacheKey(other, "hello", "word") != ck, "cache respects model");
            Check(TranslationRules.CacheKey(s, "hello", "translate") != ck, "cache respects lookup mode");
            other = s.Copy(); other.BaseUrl = url.Replace("/v1", "/v2");
            Check(TranslationRules.CacheKey(other, "hello", "word") != ck, "cache respects provider");
            for (var i = 0; i < 201; i++) store.PutCache("entry-" + i, "value-" + i);
            Check(store.CacheCount == 200 && store.GetCache("entry-0") == null && store.GetCache("entry-200") != null, "cache limit and eviction");
            store.ClearCache();
            Check(new LocalStore(store.Folder).CacheCount == 0, "clear cache persists");
            var day = store.BeginRequest();
            store.FinishRequest(day, new TranslationResult { Text = "ok", HasUsage = true, InputTokens = 20, OutputTokens = 10 });
            store.BeginRequest(); // canceled/failed request with unknown billing
            store.HitCache();
            restored = new LocalStore(store.Folder);
            Check(restored.Usage.Total.Requests == 2 && restored.Usage.Total.Input == 20 && restored.Usage.Total.Output == 10 && restored.Usage.Total.Unknown == 1 && restored.Usage.Total.CacheHits == 1, "accurate known usage and unknown request accounting");
            var client = new ApiClient();
            s = Config(url, "stream-chat");
            var deltas = new StringBuilder();
            var result = await client.TranslateAsync(s, "local-test-key", "The model learns from data.", "translate", d => deltas.Append(d), CancellationToken.None);
            Check(result.Text == "模型从数据中学习。" && deltas.ToString() == result.Text, "incremental UTF-8 chat streaming");
            Check(result.HasUsage && result.InputTokens == 123 && result.OutputTokens == 12, "final stream usage with empty choices");
            s.Model = "multiline-sse";
            result = await client.TranslateAsync(s, "local-test-key", "hello", "translate", d => { }, CancellationToken.None);
            Check(result.Text == "你好", "SSE multiline data and comments");
            s.Model = "json-chat";
            result = await client.TranslateAsync(s, "local-test-key", "hello", "word", d => { }, CancellationToken.None);
            Check(result.Text.Contains("你好") && result.HasUsage, "nonstream JSON fallback");
            s.Model = "stream-responses"; s.Protocol = "responses";
            result = await client.TranslateAsync(s, "local-test-key", "hello", "translate", d => { }, CancellationToken.None);
            Check(result.Text == "你好，世界。" && result.HasUsage && result.InputTokens == 33, "Responses streaming and usage");
            s.Model = "json-responses";
            result = await client.TranslateAsync(s, "local-test-key", "hello", "translate", d => { }, CancellationToken.None);
            Check(result.Text == "完整译文" && result.HasUsage, "Responses JSON finds message after reasoning item");
            s.Protocol = "chat"; s.Model = "truncated";
            result = await client.TranslateAsync(s, "local-test-key", "hello", "translate", d => { }, CancellationToken.None);
            Check(result.Incomplete && result.Text == "部分", "output truncation is flagged");
            s.Protocol = "responses"; s.Model = "incomplete-responses";
            result = await client.TranslateAsync(s, "local-test-key", "hello", "translate", d => { }, CancellationToken.None);
            Check(result.Incomplete && result.Text == "部分响应", "Responses truncation is flagged");
            s.Protocol = "chat"; s.Model = "no-usage";
            result = await client.TranslateAsync(s, "local-test-key", "hello", "translate", d => { }, CancellationToken.None);
            Check(!result.HasUsage && result.Text == "你好", "missing usage is not fabricated");
            foreach (var test in new[] { "error401", "error429", "redirect", "broken", "malformed", "empty", "stream-error", "refusal" })
            {
                s.Model = test;
                var rejected = false;
                try { await client.TranslateAsync(s, "local-test-key", "hello", "translate", d => { }, CancellationToken.None); }
                catch (InvalidOperationException ex)
                {
                    rejected = !ex.Message.Contains("local-test-key");
                    if (test == "error401") rejected = rejected && ex.Message.Contains("401");
                }
                Check(rejected, "handle " + test + " without leaking credentials");
            }
            s.Model = "slow";
            using (var cts = new CancellationTokenSource())
            {
                cts.CancelAfter(250);
                var clock = Stopwatch.StartNew();
                bool canceled = false;
                try { await client.TranslateAsync(s, "local-test-key", "hello", "translate", d => { }, cts.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled && clock.ElapsedMilliseconds < 3000, "cancel stalled stream promptly");
            }
            s.Model = "headers-slow";
            using (var cts = new CancellationTokenSource(200))
            {
                bool canceled = false;
                try { await client.TranslateAsync(s, "local-test-key", "hello", "translate", d => { }, cts.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "cancel before response headers");
            }
            var catalogSettings = Config(url, "mock-translator");
            var fetched = await ModelCatalog.FetchAsync(catalogSettings, "local-test-key", CancellationToken.None);
            Check(fetched.Count == 2 && fetched[1].Id == "mock-alternative", "authenticated generic model discovery");
            Check(fetched.All(m => m.Status == "listed"), "discovery does not claim models were tested");
            var passList = ModelCatalog.Parse(Json.Decode("{\"recommended\":[{\"id\":\"paid/model\"}],\"clinePass\":[{\"id\":\"cline-pass/test-model\"},{\"id\":\"paid/model\"},{\"id\":\"cline-pass/test-model\"}]}"), true);
            Check(passList.Count == 1 && passList[0].Id == "cline-pass/test-model", "ClinePass directory excludes pay-as-you-go IDs and duplicates");
            catalogSettings.Models = fetched;
            catalogSettings.CatalogIdentity = ModelCatalog.Identity(catalogSettings, "local-test-key");
            fetched[0].Status = "verified"; fetched[0].SupportsThinkingOff = true;
            var merged = ModelCatalog.Merge(new List<ModelInfo> { new ModelInfo { Id = "mock-translator" } }, catalogSettings, catalogSettings.CatalogIdentity);
            Check(merged[0].Status == "verified" && merged[0].SupportsThinkingOff, "refresh retains validation and thinking compatibility");
            merged = ModelCatalog.Merge(new List<ModelInfo> { new ModelInfo { Id = "mock-translator", Status = "listed" } }, catalogSettings, "different-account");
            Check(merged[0].Status == "listed" && !merged[0].SupportsThinkingOff, "validation is isolated across credentials");
            var mandatory = Config("https://api.cline.bot/api/v1", "cline-pass/mandatory"); mandatory.DisableThinking = true;
            Check(!ApiClient.Payload(mandatory, "hello", "translate").ContainsKey("reasoning"), "unknown Cline models keep their default thinking behavior");
            mandatory.Models.Add(new ModelInfo { Id = mandatory.Model, SupportsThinkingOff = true });
            Check(ApiClient.Payload(mandatory, "hello", "translate").ContainsKey("reasoning"), "verified compatible Cline models can disable thinking");
            mandatory.DisableThinking = false;
            Check(!ApiClient.Payload(mandatory, "hello", "translate").ContainsKey("reasoning"), "thinking preference can be disabled");
            s = Config(url, "wrapped-chat");
            result = await client.TranslateAsync(s, "local-test-key", "hello", "translate", d => { }, CancellationToken.None);
            Check(result.Text == "封装译文" && result.ReasoningTokens == 5 && result.OutputTokens == 8, "Cline wrapped JSON and reasoning usage");
            s.Model = "finish-error";
            bool streamFailed = false;
            try { await client.TranslateAsync(s, "local-test-key", "hello", "translate", d => { }, CancellationToken.None); }
            catch (InvalidOperationException) { streamFailed = true; }
            Check(streamFailed, "finish_reason error cannot become a cached success");
        }
    }
}
