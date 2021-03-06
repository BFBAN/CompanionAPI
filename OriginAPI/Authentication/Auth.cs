﻿using Newtonsoft.Json;
using OriginAPI;
using OriginAPI.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using WebClient;

namespace Origin.Authentication
{
    public class Auth
    {
        const string API = "https://api1.origin.com/";
        private GZipWebClient client;
        private GZipWebClient userClient;

        private AccessTokenModelOrigin _originAccess;
        private AccessTokenModelCompanion _companionAccess;
        private UserPid _myself;

        private string Email;
        private string Password;

        /// <summary>
        /// Get the Origin authentication token (Login needed)
        /// </summary>
        public string OriginToken {
            get {
                if (_originAccess == null) {
                    return null;
                }
                if (_originAccess.Expired) {
                    FetchOriginAccessToken();
                }
                return _originAccess.AccessToken;
            }
        }

        /// <summary>
        /// Get the Companion authentication token (Login needed)
        /// </summary>
        public string CompanionToken { get { return _companionAccess?.AccessToken; } }

        public Auth() {
            client = new GZipWebClient();
            userClient = new GZipWebClient();
        }

        public void Login(string email, string password) {
            Email = email;
            Password = password;

            try {
                // Initializing
                client.DownloadString("https://accounts.ea.com/connect/auth?response_type=code&client_id=ORIGIN_SPA_ID&display=originXWeb/login&locale=en_US&redirect_uri=https://www.origin.com/views/login.html");
                client.DownloadString(client.ResponseHeaders["Location"]);
                var loginRequestUrl = $"https://signin.ea.com{client.ResponseHeaders["Location"]}";
                client.DownloadString(loginRequestUrl); // Downloads the login page

                // Login
                var reqparm = new System.Collections.Specialized.NameValueCollection();
                reqparm.Add("email", email);
                reqparm.Add("password", password);
                reqparm.Add("_eventId", "submit");
                reqparm.Add("showAgeUp", "true");
                reqparm.Add("googleCaptchaResponse", "");
                reqparm.Add("_rememberMe", "on");
                reqparm.Add("rememberMe", "on");
                byte[] responsebytes = client.UploadValues(loginRequestUrl, "POST", reqparm);
                string responsebody = Encoding.UTF8.GetString(responsebytes);

                var arr = responsebody.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
                var nextLocation = arr.FirstOrDefault(x => x.Contains("window.location"));
                var startIndex = nextLocation.IndexOf("\"");
                loginRequestUrl = nextLocation.Substring(startIndex + 1, nextLocation.Length - (startIndex + 2));

                // TODO: Check if these are even correct
                var endOrChallenge = client.DownloadString(loginRequestUrl);
                if (endOrChallenge.Contains("&_eventId=challenge")) {
                    LoginVerification(loginRequestUrl);
                }
                else if (endOrChallenge.Contains("&_eventId=end")) {
                    client.DownloadString(loginRequestUrl + "&_eventId=end");
                }

                // Finish login
                client.DownloadString(client.ResponseHeaders["Location"]);

                FetchOriginAccessToken();
                FetchCompanionAccessToken();
                FetchMyself();
            }
            catch (Exception e) {
                Debug.WriteLine($"Error: {e.Message}");
                throw new ApplicationException("Incorrect email or password!");
            }
        }

        /// <summary>
        /// Login again after the BTFL_SESSID has expired
        /// </summary>
        public void ReLogin() {
            client = new GZipWebClient();
            userClient = new GZipWebClient();
            Login(Email, Password);
        }

        // Apparently used in different authentication
        ///// <summary>
        ///// Check if authentication is still active
        ///// </summary>
        //public bool IsAuthenticated {
        //    get {
        //        try {
        //            var result = client.DownloadString($"https://www.battlefield.com/service/auth.json");
        //            var deserialized = JsonConvert.DeserializeObject<BattlefieldAuth>(result);
        //            return deserialized.Authenticated;
        //        }
        //        catch (Exception e) {
        //            Console.WriteLine($"Error: {e.Message}");
        //            return false;
        //        }
        //    }
        //}

        private void LoginVerification(string loginRequestUrl) {
            client.DownloadString(loginRequestUrl + "&_eventId=challenge");
            loginRequestUrl = $"https://signin.ea.com{client.ResponseHeaders["Location"]}";
            client.DownloadString(loginRequestUrl); // HTML Page for login verification type input

            string type = Prompt.ShowTypeDialog();

            var reqparm = new System.Collections.Specialized.NameValueCollection {
                { "_eventId", "submit" },
                { "codeType", type } // EMAIL or APP
            };
            client.UploadValues(loginRequestUrl, "POST", reqparm);

            loginRequestUrl = $"https://signin.ea.com{client.ResponseHeaders["Location"]}";
            client.DownloadString(loginRequestUrl); // HTML Page for login verification code input

            string promptValue = Prompt.ShowDialog("Verification code", "Login Verification");

            reqparm = new System.Collections.Specialized.NameValueCollection {
                { "_eventId", "submit" },
                { "_trustThisDevice", "on" },
                { "oneTimeCode", promptValue }, // The code
                { "trustThisDevice", "on" }
            };
            client.UploadValues(loginRequestUrl, "POST", reqparm);
        }

        private void FetchMyself() {
            client.Authorization = OriginToken;
            var result = client.DownloadString("https://gateway.ea.com/proxy/identity/pids/me");
            _myself = JsonConvert.DeserializeObject<UserPid>(result);
        }

        private void FetchOriginAccessToken() {
            // Origin
            var resultJson = client.DownloadString("https://accounts.ea.com/connect/auth?client_id=ORIGIN_JS_SDK&response_type=token&redirect_uri=nucleus:rest&prompt=none");
            _originAccess = JsonConvert.DeserializeObject<AccessTokenModelOrigin>(resultJson);
        }

        private void FetchCompanionAccessToken() {
            // Companion
            var resultJson = client.DownloadString("https://accounts.ea.com/connect/auth?client_id=sparta-companion-web&response_type=code&prompt=none&redirect_uri=nucleus:rest");
            _companionAccess = JsonConvert.DeserializeObject<AccessTokenModelCompanion>(resultJson);
        }

        /// <summary>
        /// Search for PersonaId based on Username
        /// </summary>
        /// <param name="searchTerm">Username to search for (be specific)</param>
        /// <param name="user">Result user (if any)</param>
        /// <param name="status">Status of the search</param>
        /// <returns>Success/Fail</returns>
        public bool SearchPersonaId(string searchTerm, out AtomUser user, out UserSearchStatus status) {
            userClient.AuthToken = OriginToken;
            var result = userClient.DownloadString($"{API}/xsearch/users?userId={_myself.pid.pidId}&searchTerm={searchTerm}");
            var personas = JsonConvert.DeserializeObject<Personas>(result);

            user = null;
            if (personas.totalCount > 20) {
                status = UserSearchStatus.TOO_MANY_RESULTS;
                return false;
            }
            else if (personas.totalCount <= 0) {
                status = UserSearchStatus.NO_MATCHES;
                return false;
            }

            //result = userClient.DownloadString($"{API}/atom/users?userIds={string.Join(",", personas.infoList.Select(x => x.friendUserId))}");
            //var atomUsers = new XMLSerializer().Deserialize<AtomUsers>(result);
            // Apparently 5 at a time is a max
            var atomUsers = GetAtomUsers(personas.infoList);

            user = atomUsers.Users.FirstOrDefault(x => x.EAID.Equals(searchTerm, StringComparison.OrdinalIgnoreCase));
            if (user != null) {
                status = UserSearchStatus.SUCCESS;
                user.Avatar = GetAvatar(user.UserId);
                return true;
            }
            else {
                status = UserSearchStatus.PARTIAL_MATCHES;
                return false;
            }
        }

        public bool SearchPersonaId(string userId, out AtomUser user) {
            userClient.AuthToken = OriginToken;

            var atomUsers = GetAtomUsers(new List<InfoList> { new InfoList { friendUserId = userId } });

            user = atomUsers.Users.FirstOrDefault();
            if (atomUsers.Users.Count == 1) {
                user.Avatar = GetAvatar(user.UserId);
                return true;
            }

            return false;
        }

        private AtomUsers GetAtomUsers(List<InfoList> list) {
            var groups = ListExtensions.ChunkBy(list, 5);

            var atomUsers = new AtomUsers { Users = new List<AtomUser>() };
            foreach (var group in groups) {
                var result = userClient.DownloadString($"{API}/atom/users?userIds={string.Join(",", group.Select(x => x.friendUserId))}");
                var tempUsers = new XMLSerializer().Deserialize<AtomUsers>(result);
                atomUsers.Users = atomUsers.Users.Concat(tempUsers.Users).ToList();
            }

            return atomUsers;
        }

        public string GetAvatar(string userId) {
            try {
                var result = userClient.DownloadString($"{API}/avatar/user/{userId}/avatars?size=1");
                var user = new XMLSerializer().Deserialize<Users>(result);
                return user.User.Avatar.Link;
            }
            catch (Exception e) {
                return "https://d1oj1wlo31gm2u.cloudfront.net/avatars/NAV/208x208.jpg";
            }
        }
    }
}
