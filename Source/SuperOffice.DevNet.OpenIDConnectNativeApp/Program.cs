using IdentityModel.OidcClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace SuperOffice.DevNet.OpenIDConnectNativeApp
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("+-----------------------+");
            Console.WriteLine("|  Sign in with OIDC    |");
            Console.WriteLine("+-----------------------+");
            Console.WriteLine("");
            Console.WriteLine("Press any key to sign in...");
            Console.ReadKey();

            Program p = new Program();
            p.SignIn();

            Console.ReadKey();
        }

        private async void SignIn()
        {
            // create a redirect URI using an available port on the loopback address.
            string redirectUri = string.Format("http://127.0.0.1:7890/desktop-callback/");
            Console.WriteLine("redirect URI: " + redirectUri);

            // create an HttpListener to listen for requests on that redirect URI.
            var http = new HttpListener();
            http.Prefixes.Add(redirectUri);
            Console.WriteLine("Listening..");
            http.Start();

            var options = new OidcClientOptions
            {
                Authority = "https://sod.superoffice.com/login",
                LoadProfile = false,
                ClientId = "YOUR_APPLICATION_ID",
                ClientSecret = "YOUR_APPLICATION_TOKEN",
                Scope = "openid profile api",
                RedirectUri = "http://localhost:7890/desktop-callback",
                ResponseMode = OidcClientOptions.AuthorizeResponseMode.FormPost,
                Flow = OidcClientOptions.AuthenticationFlow.Hybrid,
            };

            options.Policy.Discovery.ValidateIssuerName = false;
            options.Policy.RequireAccessTokenHash = false;

            var client = new OidcClient(options);
            var state = await client.PrepareLoginAsync();

            Console.WriteLine($"Start URL: {state.StartUrl}");

            // open system browser to start authentication
            Process.Start(state.StartUrl);

            // wait for the authorization response.
            var context = await http.GetContextAsync();

            var formData = GetRequestPostData(context.Request);


            // Brings the Console to Focus.
            BringConsoleToFront();

            // sends an HTTP response to the browser.
            var response = context.Response;

            // create HTML to send to the browser
            string responseString = @"<html>
                                        <head>
                                            <meta http-equiv='refresh' 
                                                  content='5;url=https://community.superoffice.com'>
                                        </head>
                                        <body>
                                            <h1>Redirecting you to the SuperOffice Community...</h1>
                                        </body>
                                    </html>";

            // convert it to a byte[] format
            var buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;

            // get the response output stream to write to
            var responseOutput = response.OutputStream;

            // write the HTML to the output stream 
            // and wait until it is done before closing the stream.
            await responseOutput.WriteAsync(buffer, 0, buffer.Length);
            responseOutput.Close();

            Console.WriteLine($"Form Data: {formData}");
            var result = await client.ProcessResponseAsync(formData, state);

            if (!result.IsError)
            {
                Console.WriteLine("\n\nClaims:");

                foreach (var claim in result.User.Claims)
                {
                    Console.WriteLine("{0}: {1}", claim.Type, claim.Value);
                }

                Console.WriteLine();
                Console.WriteLine("Access token:\n{0}", result.AccessToken);

                if (!string.IsNullOrWhiteSpace(result.RefreshToken))
                {
                    Console.WriteLine("Refresh token:\n{0}", result.RefreshToken);
                }

                string netserverUrl = result.User.Claims.Where(c => c.Type.Contains("netserver_url")).Select(n => n.Value).FirstOrDefault();

            }
            else
            {
                Console.WriteLine("\n\nError:\n{0}", result.Error);
            }

            http.Stop();
        }

        // Hack to bring the Console window to front.
        // ref: http://stackoverflow.com/a/12066376
        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        public void BringConsoleToFront()
        {
            SetForegroundWindow(GetConsoleWindow());
        }

        public static string GetRequestPostData(HttpListenerRequest request)
        {
            if (!request.HasEntityBody)
            {
                return null;
            }

            using (var body = request.InputStream)
            {
                using (var reader = new System.IO.StreamReader(body, request.ContentEncoding))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}
