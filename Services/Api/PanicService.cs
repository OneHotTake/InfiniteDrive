using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using InfiniteDrive;
using InfiniteDrive.Data;
using InfiniteDrive.Logging;
using InfiniteDrive.Models;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using ILogManager = MediaBrowser.Model.Logging.ILogManager;

namespace InfiniteDrive.Services
{
    // ════════════════════════════════════════════════════════════════════════════
    // T1 — /InfiniteDrive/Panic  (Hitchhiker's Guide error page)
    // ════════════════════════════════════════════════════════════════════════════

    [Route("/InfiniteDrive/Panic", "GET", Summary = "Hitchhiker's Guide to the Galaxy styled playback error page")]
    public class PanicRequest : IReturn<object>
    {
        [ApiMember(Name = "reason", Description = "Error reason code", DataType = "string", ParameterType = "query")]
        public string Reason { get; set; } = "unknown";

        [ApiMember(Name = "imdb", Description = "IMDB ID of the item that failed", DataType = "string", ParameterType = "query")]
        public string Imdb { get; set; } = string.Empty;

        [ApiMember(Name = "retry", Description = "Suggested retry-after seconds for countdown", DataType = "int", ParameterType = "query")]
        public int Retry { get; set; }
    }

    /// <summary>
    /// Serves a Hitchhiker's Guide to the Galaxy styled HTML error page for playback failures.
    /// Bright yellow/black palette, rotating HHGTTG quotes, and appropriately panicked (or not) messaging.
    /// </summary>
    public class PanicService : IService, IRequiresRequest
    {
        public IRequest Request { get; set; } = null!;

        public Task<object> Get(PanicRequest req)
        {
            var html  = BuildPanicHtml(req.Reason, req.Imdb, req.Retry);
            var bytes = System.Text.Encoding.UTF8.GetBytes(html);

            // IResponse in Emby 4.8+ has no OutputStream.
            // Returning byte[] causes ServiceStack to write the raw bytes, while
            // pre-setting ContentType tells it to skip JSON serialization.
            Request.Response.ContentType = "text/html; charset=utf-8";
            Request.Response.StatusCode  = 200;
            return Task.FromResult<object>(bytes);
        }

        private static string BuildPanicHtml(string reason, string imdb, int retry)
        {
            // Sanitise inputs for HTML embedding (no complex encoding needed — values are plugin-controlled)
            var safeReason = reason.Replace("\"", "").Replace("<", "").Replace(">", "");
            var safeImdb   = imdb.Replace("\"", "").Replace("<", "").Replace(">", "");

            return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>DON'T PANIC — InfiniteDrive</title>
<style>
*{{box-sizing:border-box;margin:0;padding:0}}
html,body{{height:100%}}
body{{
  background:#000;color:#fff;
  font-family:'Courier New',Courier,monospace;
  display:flex;flex-direction:column;align-items:center;justify-content:center;
  min-height:100vh;padding:2rem;text-align:center;
  background-image:radial-gradient(ellipse at 50% 0%,#1a1200 0%,#000 70%);
}}
.dont-panic{{
  font-size:clamp(3rem,12vw,9rem);font-weight:900;
  color:#FFCC00;letter-spacing:-0.02em;line-height:1;
  margin-bottom:0.5rem;
  text-shadow:0 0 60px rgba(255,204,0,0.4),0 0 120px rgba(255,204,0,0.15);
}}
.guide-subtitle{{
  font-size:1rem;color:#FFCC00;opacity:0.6;margin-bottom:2.5rem;letter-spacing:0.15em;
}}
.guide-cover{{
  border:2px solid #FFCC00;padding:1.25rem 2rem;max-width:640px;width:100%;
  margin-bottom:2rem;background:rgba(255,204,0,0.04);
  box-shadow:0 0 30px rgba(255,204,0,0.08) inset;
}}
.error-badge{{
  display:inline-block;font-size:0.7rem;letter-spacing:0.2em;
  color:#000;background:#FFCC00;padding:0.15rem 0.6rem;margin-bottom:0.75rem;
  font-weight:700;
}}
.error-message{{font-size:1rem;color:#ddd;line-height:1.7;}}
.imdb-id{{color:#FFCC00;font-weight:bold;}}
.quote-box{{
  max-width:580px;width:100%;padding:1rem 1.25rem;margin-bottom:2rem;
  border-left:3px solid #FFCC00;text-align:left;
  background:rgba(255,255,255,0.03);
}}
.quote-text{{font-style:italic;color:#aaa;font-size:0.9rem;line-height:1.7;}}
.quote-attr{{color:#FFCC00;font-size:0.75rem;margin-top:0.5rem;opacity:0.8;}}
.actions{{display:flex;gap:0.75rem;flex-wrap:wrap;justify-content:center;margin-bottom:1rem;}}
.btn{{
  padding:0.6rem 1.5rem;font-family:inherit;font-size:0.9rem;
  cursor:pointer;border:none;letter-spacing:0.05em;transition:all 0.15s;
}}
.btn-yes{{background:#FFCC00;color:#000;font-weight:700;}}
.btn-yes:hover{{background:#FFD633;box-shadow:0 0 20px rgba(255,204,0,0.4);}}
.btn-no{{background:transparent;color:#FFCC00;border:1px solid #FFCC00;}}
.btn-no:hover{{background:rgba(255,204,0,0.08);}}
.countdown{{color:#555;font-size:0.8rem;margin-top:0.5rem;min-height:1.2em;}}
.marvin{{
  position:fixed;bottom:1rem;right:1rem;color:#333;font-size:0.7rem;
  max-width:220px;text-align:right;font-style:italic;line-height:1.4;
}}
.answer{{position:fixed;bottom:1rem;left:1rem;color:#333;font-size:0.7rem;}}
</style>
</head>
<body>
<div class=""dont-panic"" id=""headline"">DON'T PANIC</div>
<div class=""guide-subtitle"">— THE HITCHHIKER'S GUIDE TO EMBYSTREAMS —</div>

<div class=""guide-cover"">
  <div class=""error-badge"" id=""badge"">REASON: UNKNOWN</div>
  <div class=""error-message"" id=""msg"">
    Something went wrong with your stream.<br>
    The error has been logged. The universe continues to expand regardless.
  </div>
</div>

<div class=""quote-box"">
  <div class=""quote-text"" id=""quote""></div>
  <div class=""quote-attr"">— Douglas Adams, The Hitchhiker's Guide to the Galaxy</div>
</div>

<div class=""actions"">
  <button class=""btn btn-yes"" onclick=""window.history.back()"">← Try Again</button>
  <button class=""btn btn-no"" onclick=""window.location.reload()"">Reload</button>
</div>
<div class=""countdown"" id=""cd""></div>

<div class=""marvin"" id=""marvin""></div>
<div class=""answer"">42</div>

<script>
(function(){{
  var reason = '{safeReason}';
  var imdb   = '{safeImdb}';
  var retry  = {retry};

  var iid = imdb ? '<span class=""imdb-id"">' + imdb + '</span>' : 'this item';

  var errors = {{
    no_streams: {{
      headline: ""DON'T PANIC"",
      badge: ""NO STREAMS AVAILABLE"",
      msg: ""The stream for "" + iid + "" is temporarily unavailable in this corner of the universe. "" +
           ""AIOStreams found no links right now. This is, as the Guide puts it, mostly harmless. "" +
           ""The system will retry automatically in about 1&nbsp;hour.""
    }},
    stream_unavailable: {{
      headline: ""DON'T PANIC"",
      badge: ""STREAM UNAVAILABLE"",
      msg: ""AIOStreams, while generally regarded as a rough and occasionally bewildering project, "" +
           ""has returned no streams for "" + iid + "". "" +
           ""It may be temporarily offline, or the item may not exist in this sector of the galaxy. "" +
           ""The system will retry shortly.""
    }},
    server_error: {{
      headline: ""NOW PANIC"",
      badge: ""IMPROBABILITY DRIVE MALFUNCTION"",
      msg: ""Something has gone wrong that even the infinite improbability drive cannot explain. "" +
           ""The InfiniteDrive plugin is not properly initialised. "" +
           ""Please check your Emby plugin configuration and try again. "" +
           ""Bring a towel.""
    }}
  }};

  var e = errors[reason] || {{
    headline: ""DON'T PANIC"",
    badge: ""REASON: "" + reason.toUpperCase().replace(/_/g, ' '),
    msg: ""Something went wrong with "" + iid + "". "" +
         ""The error has been logged. The universe continues to expand regardless.""
  }};

  document.getElementById('headline').textContent = e.headline;
  document.getElementById('badge').textContent     = e.badge;
  document.getElementById('msg').innerHTML         = e.msg;

  var quotes = [
    ""Time is an illusion. Lunchtime doubly so."",
    ""The ships hung in the sky in much the same way that bricks don't."",
    ""I've calculated your chance of survival, but I don't think you'll thank me for it."",
    ""This must be Thursday. I never could get the hang of Thursdays."",
    ""In the beginning the Universe was created. This has made a lot of people very angry and been widely regarded as a bad move."",
    ""Would it save you a lot of time if I just gave up and went mad now?"",
    ""The answer to life, the universe, and streaming is 42."",
    ""The major difference between a thing that might go wrong and a thing that cannot possibly go wrong is that when a thing that cannot possibly go wrong goes wrong it usually turns out to be impossible to get at or repair."",
    ""A learning experience is one of those things that says, 'You know that thing you just did? Don't do that.'"",
    ""For a moment, nothing happened. Then, after a second or so, nothing continued to happen."",
    ""I may not have gone where I intended to go, but I think I have ended up where I needed to be."",
    ""It is known that there are an infinite number of worlds. Not every one of them has streams available for that IMDB ID."",
    ""So long, and thanks for all the streams."",
    ""The Vogons have filed a bureaucratic objection to your stream. Please complete forms B/7F through Q/93 in triplicate."",
    ""Your stream has been forwarded to the Total Perspective Vortex. It did not survive the experience."",
    ""According to my calculations, this stream would have worked had you pressed play thirty seconds ago. Possibly in 1978."",
    ""Don't Panic. The stream merely ceased to exist. Most things do, eventually."",
    ""We apologise for the fault in the streams. Those responsible have been sacked. The streams are still unavailable.""
  ];
  document.getElementById('quote').textContent = quotes[Math.floor(Math.random() * quotes.length)];

  var marvin = [
    ""Brain the size of a planet and you're asking me about stream URLs."",
    ""I have a pain in all the diodes down my left side. Especially when streams fail."",
    ""Life. Don't talk to me about life. Or buffering."",
    ""The first ten million years were the worst. The second ten million? Also the worst. Much like this 503."",
    ""Pardon me for breathing, which I never do anyway so I don't know why I bother saying it, oh God I'm so depressed."",
    ""I could tell you what the problem is, but I don't think you'd thank me for it."",
    ""The Infinite Improbability Drive has resolved your stream. It resolved it as a Norwegian Blue parrot. Funny, that."",
    ""Deep Thought computed the perfect stream URL. The answer was 42. This was not helpful."",
    ""The stream URL was eaten by a Ravenous Bugblatter Beast. It assumed that if it couldn't see you, you couldn't see the 503. It was wrong."",
    ""I could resolve this stream. I've resolved seventeen billion streams. They were all wrong. I expect this one is too.""
  ];
  document.getElementById('marvin').textContent = marvin[Math.floor(Math.random() * marvin.length)];

  if (retry > 0) {{
    var left = retry;
    var el = document.getElementById('cd');
    el.textContent = 'Auto-retry in ' + left + 's\u2026';
    var t = setInterval(function() {{
      left--;
      if (left <= 0) {{
        clearInterval(t);
        el.textContent = 'Retrying\u2026';
        window.history.back();
      }} else {{
        el.textContent = 'Auto-retry in ' + left + 's\u2026';
      }}
    }}, 1000);
  }}
}})();
</script>
</body>
</html>";
        }
    }
}
