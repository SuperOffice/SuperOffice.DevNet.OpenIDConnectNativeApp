using IdentityModel.Client;
using IdentityModel.OidcClient;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using SuperOffice.WebApi.Agents;
using D = SuperOffice.WebApi.Data;

namespace SuperOffice.DevNet.WebApiClient.Test
{
    class Program
    {
        // *************************************************************************************************
        // **** SuperOffice settings ***********************************************************************
        // *************************************************************************************************
        private static WebApi.Data.OnlineEnvironment _onlineEnvironment = WebApi.Data.OnlineEnvironment.SOD;
        private static string _systemUserToken = "YOUR APP-ABCD123456";
        private static string _customerId      = "Cust12345";
        private static string _authority       = "https://sod.superoffice.com/login/";
        private static string _clientId        = "YOUR_CLIENT_ID";
        private static string _clientSecret    = "YOUR_CLIENT_SECRET";
        // *************************************************************************************************
        // *************************************************************************************************
        // *************************************************************************************************

        static OidcClient _oidcClient;

        public static void Main(string[] args) => MainAsync().GetAwaiter().GetResult();

        public static async Task MainAsync()
        {
            Console.WriteLine("+-----------------------+");
            Console.WriteLine("|  Sign in with OIDC    |");
            Console.WriteLine("+-----------------------+");
            Console.WriteLine("");
            Console.WriteLine("Press any key to sign in...");
            Console.ReadKey();

            await Login();
        }

        private static async Task Login()
        {
            // create a redirect URI using an available port on the loopback address.
            // requires the OP to allow random ports on 127.0.0.1 - otherwise set a static port
            var browser = new SystemBrowser();
            string redirectUri = string.Format($"http://127.0.0.1:{browser.Port}");

            var options = new OidcClientOptions
            {
                Authority = _authority,
                ClientId = _clientId,
                ClientSecret = _clientSecret, // Client secret is not used to obtain an Access/Refresh/ID tokens
                RedirectUri = redirectUri,    //- the client secret is only required when using the refreshToken to get a new access token.
                Scope = "openid",
                FilterClaims = false,
                LoadProfile = false,
                Browser = browser,
                Flow = OidcClientOptions.AuthenticationFlow.AuthorizationCode,
                ResponseMode = OidcClientOptions.AuthorizeResponseMode.Redirect
            };

            var serilog = new LoggerConfiguration()
                 .MinimumLevel.Verbose()
                 .Enrich.FromLogContext()
                 .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {SourceContext}{NewLine}{Message}{NewLine}{Exception}{NewLine}")
                 .CreateLogger();

            options.LoggerFactory.AddSerilog(serilog);

            _oidcClient = new OidcClient(options);
            _oidcClient.Options.Policy.ValidateTokenIssuerName = false;
            _oidcClient.Options.Policy.Discovery.ValidateIssuerName = false;
            var result = await _oidcClient.LoginAsync(new LoginRequest());

            ShowResult(result);
            await NextSteps(result);
        }

        private static void ShowResult(LoginResult result)
        {
            if (result.IsError)
            {
                Console.WriteLine("\n\nError:\n{0}", result.Error);
                return;
            }

            Console.WriteLine("\n\nClaims:");
            foreach (var claim in result.User.Claims)
            {
                Console.WriteLine("{0}: {1}", claim.Type, claim.Value);
            }

            Console.WriteLine($"\nidentity token: {result.IdentityToken}");
            Console.WriteLine($"access token:   {result.AccessToken}");
            Console.WriteLine($"refresh token:  {result?.RefreshToken ?? "none"}");


        }

        private static async Task NextSteps(LoginResult result)
        {
            var currentAccessToken = result.AccessToken;
            var currentRefreshToken = result.RefreshToken;

            var menu = "  x...exit  c...call api   ";
            if (currentRefreshToken != null) menu += "r...refresh token   ";

            while (true)
            {
                Console.WriteLine("\n\n");

                Console.Write(menu);
                var key = Console.ReadKey();

                if (key.Key == ConsoleKey.X) return;
                if (key.Key == ConsoleKey.C) await CallApi(result);
                if (key.Key == ConsoleKey.R)
                {
                    var refreshResult = await _oidcClient.RefreshTokenAsync(currentRefreshToken);
                    if (refreshResult.IsError)
                    {
                        Console.WriteLine($"Error: {refreshResult.Error}");
                    }
                    else
                    {
                        currentRefreshToken = refreshResult.RefreshToken;
                        currentAccessToken = refreshResult.AccessToken;

                        Console.WriteLine("\n\n");
                        Console.WriteLine($"access token:   {refreshResult.AccessToken}");
                        Console.WriteLine($"refresh token:  {refreshResult?.RefreshToken ?? "none"}");
                    }
                }
            }
        }

        private static async Task CallApi(LoginResult result)
        {
            // extract the webapi url from the user claims...

            var webapi_url = result.User.Claims.Where(c => c.Type.Contains("webapi_url", StringComparison.InvariantCultureIgnoreCase)).Select(c => c.Value).FirstOrDefault();

            // get system user ticket ( 6 hour duration with sliding expiration )
            
            var systemUserInfo = new WebApi.IdentityModel.SystemUserInfo()
            {
                ClientSecret = _clientSecret,
                ContextIdentifier = _customerId,
                Environment = _onlineEnvironment,
                PrivateKey = GetPrivateKey(),
                SystemUserToken = _systemUserToken
            };

            var systemUserClient = new SuperOffice.WebApi.IdentityModel.SystemUserClient(systemUserInfo);
            var ticketCredential = await systemUserClient.GetSystemUserTicketAsync();

            // use the SuperOffice.WebApi Agents to access the Agent web service endpoints

            // construct the system user authorization (since client credentials is not yet supported)

            var systemUserAuth = new WebApi.AuthorizationSystemUserTicket(systemUserInfo, ticketCredential);
            var config = new SuperOffice.WebApi.WebApiOptions(webapi_url, systemUserAuth);

            // use the archive agent to perform a search
            // https://community.superoffice.com/documentation/sdk/SO.NetServer.Web.Services/html/Reference-WebAPI-REST-Search.htm

            var archiveAgent = new ArchiveAgent(config);
            var sales = await GetSalesBetweenDates(archiveAgent, DateTime.Now.AddMonths(-1), DateTime.Now);
            WriteToConsole(sales);

            var sales2 = await GetSalesAfterDatetime(archiveAgent, DateTime.Now.AddDays(-1));
            WriteToConsole(sales2);
        }

        private async static Task<D.ArchiveListItem[]> GetSalesBetweenDates(ArchiveAgent archiveAgent, DateTime start, DateTime end)
        {
            return await archiveAgent.GetArchiveListByColumns2Async(
                "Sale",
                "heading,amount,type",
                "",
                string.Format("date between ('{0}','{1}')", start.Date.ToShortDateString(), end.Date.ToShortDateString()),
                "Sale",
                0,
                int.MaxValue
            );
        }

        private async static Task<D.ArchiveListItem[]> GetSalesAfterDatetime(ArchiveAgent archiveAgent, DateTime datetime)
        {
            //Illegal data in CultureDataFormatter: Expected DateTime, got 02020-32-18T08:32:47
            string time = string.Format("registeredDate afterTime '{0}'", datetime.ToUniversalTime().ToString("yyyy-MM-ddThh:mm:ss"));
            
            return await archiveAgent.GetArchiveListByColumns2Async(
                "Sale",
                "heading,amount,type",
                "",
                time,
                "Sale",
                0,
                int.MaxValue
            );
        }

        private static void WriteToConsole(D.ArchiveListItem[] sales)
        {
            if(sales == null)
            {
                Console.WriteLine("No sales to show...");
                return;
            }

            foreach (var sale in sales)
            {
                int saleId = sale.PrimaryKey;
                string heading = sale.ColumnData["heading"].DisplayValue;
                string type = sale.ColumnData["type"].DisplayValue;
                decimal amount = D.CultureDataFormatter.ParseEncodedDecimal(sale.ColumnData["amount"].DisplayValue);
                Console.WriteLine(string.Format("Sale: {0}, Title: {1}, Amount: {2}", saleId, heading, amount.ToString()));
            }
        }

        private static string GetPrivateKey()
        {
            return @"<RSAKeyValue>
  <Modulus>******************************************************************wxBl0B</Modulus>
  <Exponent>****</Exponent>
  <P>4W+hxqoV******************************************************************AskO7UVuFeLw==</P>
  <Q>6cwuLKHZ******************************************************************zn4mjF9CKBARKw==</Q>
  <DP>DnC+JiG*************************YOUR_KEY_GOES_HERE***********************d2v/SCvTVlmVQ==</DP>
  <DQ>3FAQzvQ******************************************************************Lplnu+BCvmhAw==</DQ>
  <InverseQ>K******************************************************************C8eQGoWOwXPDo3KcSEQ==</InverseQ>
  <D>GqikViVp4OvH7+mF************************************************************************************************************************************ZRsk=</D>
</RSAKeyValue>";
        }
    }
}
