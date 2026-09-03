#nullable enable

using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JukeBox.Game.Online
{
    /// <summary>
    /// The other half of the sign-in: a throwaway HTTP server on loopback that exists only long
    /// enough for osu! to redirect the user's browser back to it with a one-time code.
    ///
    /// <para>
    /// It listens on <see cref="OsuOAuth.LOOPBACK_PORT"/> (fixed — see that constant for why an
    /// ephemeral port cannot work here), accepts exactly ONE request, answers it with a small page
    /// telling the user to go back to the app, and shuts down. Nothing else can reach it: it binds
    /// loopback only, so no other machine on the network can even connect, and it is gone within
    /// seconds either way.
    /// </para>
    /// </summary>
    public sealed class LoopbackCallbackListener : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        private readonly string expectedState;

        /// <param name="expectedState">The CSRF value from <see cref="OsuOAuth.NewState"/>; a
        /// callback carrying anything else is refused rather than trusted.</param>
        /// <param name="port">Overridable so tests can bind a free port instead of the real one.</param>
        /// <param name="path">Overridable for the same reason.</param>
        public LoopbackCallbackListener(string expectedState, int port = OsuOAuth.LOOPBACK_PORT, string path = "/callback/")
        {
            this.expectedState = expectedState;

            // Loopback only, and both spellings: the browser may resolve "localhost" to either
            // stack, and a prefix bound to just one of them fails intermittently by machine.
            listener.Prefixes.Add($"http://localhost:{port}{path}");
            listener.Prefixes.Add($"http://127.0.0.1:{port}{path}");
        }

        /// <summary>
        /// Begins listening. Separated from <see cref="WaitForCodeAsync"/> so the caller can be sure
        /// the port is actually held BEFORE opening the browser — otherwise a fast redirect can
        /// arrive before the listener exists and the sign-in fails for no visible reason.
        /// </summary>
        /// <exception cref="OsuOAuthException">The port is already in use, or the OS refused the
        /// binding — reported as something the user can act on rather than an HttpListenerException.</exception>
        public void Start()
        {
            try
            {
                listener.Start();
            }
            catch (HttpListenerException e)
            {
                throw new OsuOAuthException(
                    $"Couldn't listen on {OsuOAuth.RedirectUri} ({e.Message}). Close whatever is using port {OsuOAuth.LOOPBACK_PORT} and try again.");
            }
        }

        /// <summary>
        /// Waits for osu! to redirect the browser here, and returns the authorization code.
        /// </summary>
        /// <exception cref="OsuOAuthException">The user denied consent, the state did not match, or
        /// no code was present.</exception>
        public async Task<string> WaitForCodeAsync(CancellationToken ct = default)
        {
            // GetContextAsync has no cancellation overload; stopping the listener is what makes a
            // cancelled sign-in return promptly instead of hanging on a browser tab nobody will
            // ever complete.
            using (ct.Register(Stop))
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                catch (HttpListenerException)
                {
                    throw new OsuOAuthException("The sign-in callback was closed before osu! answered.");
                }
                catch (ObjectDisposedException)
                {
                    throw new OsuOAuthException("The sign-in callback was closed before osu! answered.");
                }

                var query = context.Request.QueryString;

                string? code = query["code"];
                string? state = query["state"];
                string? error = query["error"];

                string message = error != null
                    ? "Sign-in was cancelled. You can close this tab."
                    : "Signed in. You can close this tab and go back to osu!JukeBox.";

                await respondAsync(context, message).ConfigureAwait(false);

                if (error != null)
                    throw new OsuOAuthException(error == "access_denied" ? "You declined the osu! sign-in." : $"osu! refused the sign-in ({error}).");

                // Checked before the code is used for anything. A callback with the wrong state did
                // not come from the sign-in we started, and honouring its code would bind this app
                // to whichever account the sender chose.
                if (!string.Equals(state, expectedState, StringComparison.Ordinal))
                    throw new OsuOAuthException("The sign-in response didn't match this request and was ignored. Please try again.");

                if (string.IsNullOrEmpty(code))
                    throw new OsuOAuthException("osu! didn't return a sign-in code.");

                return code;
            }
        }

        private static async Task respondAsync(HttpListenerContext context, string message)
        {
            byte[] body = Encoding.UTF8.GetBytes(
                "<!doctype html><meta charset=\"utf-8\"><title>osu!JukeBox</title>"
                + "<body style=\"font-family:system-ui;background:#14141b;color:#fff;display:grid;place-items:center;height:100vh;margin:0\">"
                + $"<p>{WebUtility.HtmlEncode(message)}</p>");

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;

            try
            {
                await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                context.Response.Close();
            }
            catch (Exception)
            {
                // The browser hanging up before we finish writing is not a sign-in failure — the
                // code is already in hand, and that is the only thing this request was for.
            }
        }

        private void Stop()
        {
            try
            {
                if (listener.IsListening)
                    listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            Stop();
            listener.Close();
        }
    }
}
