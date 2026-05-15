using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TodoFloat.Views;

/// <summary>
/// Local stand-in for the prototype's <c>window.claude.complete</c> call.
/// Replies in Nibble's voice: soft, 1–2 short Chinese sentences, no emoji,
/// occasional ～ particle. To wire the real Claude API, replace
/// <see cref="RespondAsync"/> with an HttpClient POST to
/// https://api.anthropic.com/v1/messages — see comment block below.
/// </summary>
internal sealed class NibbleResponder
{
    private readonly Random _rng = new();

    private static readonly string[] Greetings =
    {
        "嗨～我在哦，今天怎么样？",
        "在的在的，叫我有事吗？",
        "唔～你来啦～",
        "诶嘿，又是你呀～",
    };

    private static readonly string[] Confused =
    {
        "嗯…我有点没听明白～",
        "唔，再说一遍嘛？",
        "我电路稍微卡了一下…",
    };

    private static readonly string[] Comfort =
    {
        "辛苦啦～歇会儿再继续。",
        "深呼吸一下，慢慢来嘛。",
        "我陪着你呢，不要急。",
    };

    private static readonly string[] Cheer =
    {
        "你最棒啦～继续冲！",
        "嗯！我看好你！",
        "加油加油，我在旁边守着。",
    };

    private static readonly string[] Sleepy =
    {
        "我也有点想打瞌睡了…",
        "嗯…困是因为认真过头啦。",
        "要不要一起小睡一下？",
    };

    private static readonly string[] Time =
    {
        "现在是 " + DateTime.Now.ToString("HH:mm") + " ～",
        "时间过得好快呀，你看，已经 " + DateTime.Now.ToString("HH:mm") + " 了。",
    };

    private static readonly string[] Idle =
    {
        "嗯嗯～在听你说。",
        "继续说嘛，我在认真听。",
        "我们今天聊点什么好呢？",
        "你想我陪你做点什么？",
    };

    public Task<string> RespondAsync(IReadOnlyList<(string role, string content)> history)
    {
        // Find the most recent user message
        var last = history.LastOrDefault(m => m.role == "user").content ?? "";
        var text = last.ToLowerInvariant();

        string reply;
        if (history.Count(m => m.role == "user") == 1 &&
            (text.Contains("你好") || text.Contains("嗨") || text.Contains("hi") || text.Contains("hello") || text == "在吗"))
        {
            reply = Pick(Greetings);
        }
        else if (text.Contains("累") || text.Contains("烦") || text.Contains("难过") || text.Contains("不开心"))
        {
            reply = Pick(Comfort);
        }
        else if (text.Contains("加油") || text.Contains("冲") || text.Contains("继续") || text.Contains("可以吗"))
        {
            reply = Pick(Cheer);
        }
        else if (text.Contains("困") || text.Contains("睡") || text.Contains("瞌睡"))
        {
            reply = Pick(Sleepy);
        }
        else if (text.Contains("时间") || text.Contains("几点") || text.Contains("现在"))
        {
            reply = Pick(Time);
        }
        else if (text.Contains("你是谁") || text.Contains("叫什么"))
        {
            reply = "我叫 Nibble，住在你桌面上的小电子精灵～";
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            reply = Pick(Confused);
        }
        else
        {
            reply = Pick(Idle);
        }

        // Tiny fake-thinking delay so the "想想…" indicator feels natural.
        return DelayedReturn(reply);
    }

    private async Task<string> DelayedReturn(string s)
    {
        await Task.Delay(_rng.Next(400, 900));
        return s;
    }

    private string Pick(string[] arr) => arr[_rng.Next(arr.Length)];

    // ─────────────────────────────────────────────────────────────
    // Want a real Claude reply instead of canned phrases?
    //
    // 1. Add <PackageReference Include="System.Net.Http.Json" /> if needed.
    // 2. Replace RespondAsync with something like:
    //
    //   using var http = new HttpClient();
    //   http.DefaultRequestHeaders.Add("x-api-key", "<YOUR KEY>");
    //   http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    //   var body = new {
    //       model = "claude-haiku-4-5-20251001",   // fast, cheap; or claude-sonnet-4-6
    //       max_tokens = 80,
    //       system = "你叫 Nibble，是一只住在用户桌面上的小电子精灵。" +
    //                "你说话用中文，语气软软的、有点撒娇、像 Q 版宠物。" +
    //                "回答非常简短：1-2 句话，最多 30 个字。可以用 ～ 等语气词，但不要用 emoji。",
    //       messages = history.Select(m => new {
    //           role = m.role, content = m.content
    //       }).ToArray()
    //   };
    //   var resp = await http.PostAsJsonAsync("https://api.anthropic.com/v1/messages", body);
    //   var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
    //   return json.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    //
    // Store the key in user-secrets or env var; do NOT bake it into the binary.
    // ─────────────────────────────────────────────────────────────
}
