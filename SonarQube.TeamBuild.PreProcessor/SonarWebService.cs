﻿/*
 * SonarQube Scanner for MSBuild
 * Copyright (C) 2016-2017 SonarSource SA
 * mailto:info AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */
 
using Newtonsoft.Json.Linq;
using SonarQube.Common;
using SonarQube.TeamBuild.PreProcessor.Roslyn.Model;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;

namespace SonarQube.TeamBuild.PreProcessor
{
    public sealed class SonarWebService : ISonarQubeServer, IDisposable
    {
        private const string oldDefaultProjectTestPattern = @"[^\\]*test[^\\]*$";
        private readonly string serverUrl;
        private readonly IDownloader downloader;
        private readonly ILogger logger;
        private Version serverVersion;

        public SonarWebService(IDownloader downloader, string server, ILogger logger)
        {
            if (downloader == null)
            {
                throw new ArgumentNullException("downloader");
            }
            if (string.IsNullOrWhiteSpace(server))
            {
                throw new ArgumentNullException("server");
            }
            if (logger == null)
            {
                throw new ArgumentNullException("logger");
            }

            this.downloader = downloader;
            this.serverUrl = server.EndsWith("/", StringComparison.OrdinalIgnoreCase) ? server.Substring(0, server.Length - 1) : server;
            this.logger = logger;
        }

        #region ISonarQubeServer interface

        public bool TryGetQualityProfile(string projectKey, string projectBranch, string organization, string language, out string qualityProfileKey)
        {
            string projectId = GetProjectIdentifier(projectKey, projectBranch);

            string contents;
            var ws = addOrganization(GetUrl("/api/qualityprofiles/search?projectKey={0}", projectId), organization);
            this.logger.LogDebug(Resources.MSG_FetchingQualityProfile, projectId, ws);

            qualityProfileKey = DoLogExceptions(() =>
            {
                if (!this.downloader.TryDownloadIfExists(ws, out contents))
                {
                    ws = addOrganization(GetUrl("/api/qualityprofiles/search?defaults=true"), organization);

                    this.logger.LogDebug(Resources.MSG_FetchingQualityProfile, projectId, ws);
                    contents = this.downloader.Download(ws);
                }
                var json = JObject.Parse(contents);
                var profiles = json["profiles"].Children<JObject>();

                var profile = profiles.SingleOrDefault(p => language.Equals(p["language"].ToString()));
                if (profile == null)
                {
                    return null;
                }

                return profile["key"].ToString();
            }, ws);

            return qualityProfileKey != null;
        }

        /// <summary>
        /// Retrieves rule keys of rules having a given language and that are not activated in a given profile.
        ///
        /// </summary>
        /// <param name="qprofile">Quality profile key.</param>
        /// <param name="language">Rule Language.</param>
        /// <returns>Non-activated rule keys, including repo. Example: csharpsquid:S1100</returns>
        public IList<string> GetInactiveRules(string qprofile, string language)
        {
            int fetched = 0;
            int page = 1;
            int total = 0;
            var ruleList = new List<string>();

            do
            {
                var ws = GetUrl("/api/rules/search?f=internalKey&ps=500&activation=false&qprofile={0}&p={1}&languages={2}", qprofile, page.ToString(), language);
                this.logger.LogDebug(Resources.MSG_FetchingInactiveRules, qprofile, language, ws);

                ruleList.AddRange(DoLogExceptions(() =>
                {
                    var contents = this.downloader.Download(ws);
                    var json = JObject.Parse(contents);
                    total = Convert.ToInt32(json["total"]);
                    fetched += Convert.ToInt32(json["ps"]);
                    page++;
                    var rules = json["rules"].Children<JObject>();

                    return rules.Select(r => r["key"].ToString());
                }, ws));
            } while (fetched < total);

            return ruleList;
        }

        /// <summary>
        /// Retrieves active rules from the quality profile with the given ID, including their parameters and template keys.
        ///
        /// </summary>
        /// <param name="qprofile">Quality profile id.</param>
        /// <returns>List of active rules</returns>
        public IList<ActiveRule> GetActiveRules(string qprofile)
        {
            int fetched = 0;
            int page = 1;
            int total = 0;
            var activeRuleList = new List<ActiveRule>();

            do
            {
                var ws = GetUrl("/api/rules/search?f=repo,name,severity,lang,internalKey,templateKey,params,actives&ps=500&activation=true&qprofile={0}&p={1}", qprofile, page.ToString());
                this.logger.LogDebug(Resources.MSG_FetchingActiveRules, qprofile, ws);

                activeRuleList.AddRange(DoLogExceptions(() =>
                {
                    var contents = this.downloader.Download(ws);
                    var json = JObject.Parse(contents);
                    total = Convert.ToInt32(json["total"]);
                    fetched += Convert.ToInt32(json["ps"]);
                    page++;
                    var rules = json["rules"].Children<JObject>();
                    var actives = json["actives"];

                    return rules.Select(r =>
                    {
                        ActiveRule activeRule = new ActiveRule(r["repo"].ToString(), ParseRuleKey(r["key"].ToString()));
                        if (r["internalKey"] != null)
                        {
                            activeRule.InternalKey = r["internalKey"].ToString();
                        }
                        if (r["templateKey"] != null)
                        {
                            activeRule.TemplateKey = r["templateKey"].ToString();
                        }

                        var active = actives[r["key"].ToString()];
                        this.logger.LogDebug($"Accessing active with key [{r["key"].ToString()}] (count: [{active?.Count()}]) for active rule with RepoKey [{activeRule.RepoKey}], RuleKey [{activeRule.RuleKey}], InternalKey [{activeRule.InternalKey}], TemplateKey [{activeRule.TemplateKey}] ");
                        var listParams = active.Single()["params"].Children<JObject>();
                        activeRule.Parameters = listParams.ToDictionary(pair => pair["key"].ToString(), pair => pair["value"].ToString());
                        if (activeRule.Parameters.ContainsKey("CheckId"))
                        {
                            activeRule.RuleKey = activeRule.Parameters["CheckId"];
                        }
                        return activeRule;
                    });
                }, ws));
            } while (fetched < total);

            return activeRuleList;
        }

        /// <summary>
        /// Retrieves project properties from the server.
        ///
        /// Will fail with an exception if the downloaded return from the server is not a JSON array.
        /// </summary>
        /// <param name="projectKey">The SonarQube project key to retrieve properties for.</param>
        /// <param name="projectBranch">The SonarQube project branch to retrieve properties for (optional).</param>
        /// <returns>A dictionary of key-value property pairs.</returns>
        /// 
        public IDictionary<string, string> GetProperties(string projectKey, string projectBranch = null)
        {
            if (string.IsNullOrWhiteSpace(projectKey))
            {
                throw new ArgumentNullException("projectKey");
            }
            string projectId = GetProjectIdentifier(projectKey, projectBranch);

            if (GetServerVersion().CompareTo(new Version(6, 3)) >= 0)
            {
                return GetProperties63(projectId);
            }
            else
            {
                return GetPropertiesOld(projectId);
            }
        }

        public Version GetServerVersion()
        {
            if (serverVersion == null)
            {
                DownloadServerVersion();
            }
            return serverVersion;
        }

        public IEnumerable<string> GetAllLanguages()
        {
            var ws = GetUrl("/api/languages/list");
            return DoLogExceptions(() =>
            {
                var contents = this.downloader.Download(ws);

                JArray langArray = JObject.Parse(contents).Value<JArray>("languages");
                return langArray.Select(obj => obj["key"].ToString());
            }, ws);
        }

        public bool TryDownloadEmbeddedFile(string pluginKey, string embeddedFileName, string targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(pluginKey))
            {
                throw new ArgumentNullException("pluginKey");
            }
            if (string.IsNullOrWhiteSpace(embeddedFileName))
            {
                throw new ArgumentNullException("embeddedFileName");
            }
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                throw new ArgumentNullException("targetDirectory");
            }

            string url = GetUrl("/static/{0}/{1}", pluginKey, embeddedFileName);

            return DoLogExceptions(() =>
            {
                string targetFilePath = Path.Combine(targetDirectory, embeddedFileName);

                logger.LogDebug(Resources.MSG_DownloadingZip, embeddedFileName, url, targetDirectory);
                return this.downloader.TryDownloadFileIfExists(url, targetFilePath);
            }, url);
        }

        #endregion

        #region Private methods

        private string addOrganization(string encodedUrl, string organization)
        {
            if (string.IsNullOrEmpty(organization))
            {
                return encodedUrl;
            }
            Version version = GetServerVersion();
            if (version.CompareTo(new Version(6, 3)) >= 0)
            {
                return EscapeQuery(encodedUrl + "&organization={0}", organization);
            }

            return encodedUrl;
        }

        private T DoLogExceptions<T>(Func<T> op, string url)
        {
            try
            {
                return op();
            }
            catch (Exception e)
            {
                this.logger.LogError("Failed to request and parse '{0}': {1}", url, e.Message);
                throw;
            }
        }
        private void DownloadServerVersion()
        {
            var ws = GetUrl("api/server/version");
            serverVersion = DoLogExceptions(() =>
            {
                var contents = this.downloader.Download(ws);
                int separator = contents.IndexOf('-');
                return separator >= 0 ? new Version(contents.Substring(0, separator)) : new Version(contents);
            }, ws);
        }

        private IDictionary<string, string> GetPropertiesOld(string projectId)
        {
            string ws = GetUrl("/api/properties?resource={0}", projectId);
            this.logger.LogDebug(Resources.MSG_FetchingProjectProperties, projectId, ws);
            var result = DoLogExceptions(() =>
            {
                var contents = this.downloader.Download(ws);
                var properties = JArray.Parse(contents);
                return properties.ToDictionary(p => p["key"].ToString(), p => p["value"].ToString());
            }, ws);

            return checkTestProjectPattern(result);
        }

        private IDictionary<string, string> GetProperties63(string projectId)
        {
            string ws = GetUrl("/api/settings/values?component={0}", projectId);
            this.logger.LogDebug(Resources.MSG_FetchingProjectProperties, projectId, ws);
            string contents = "";
            bool success = DoLogExceptions(() => this.downloader.TryDownloadIfExists(ws, out contents), ws);

            if (!success)
            {
                ws = GetUrl("/api/settings/values");
                this.logger.LogDebug("No settings for project {0}. Getting global settings: {1}", projectId, ws);
                contents = DoLogExceptions(() => this.downloader.Download(ws), ws);
            }

            return DoLogExceptions(() => ParseSettingsResponse(contents), ws);
        }

        private Dictionary<string, string> ParseSettingsResponse(string contents)
        {
            var settings = new Dictionary<string, string>();
            var settingsArray = JObject.Parse(contents).Value<JArray>("settings");
            foreach (var t in settingsArray)
            {
                GetPropertyValue(settings, t);
            }


            return checkTestProjectPattern(settings);
        }

        private Dictionary<string, string> checkTestProjectPattern(Dictionary<string, string> settings)
        {
            // http://jira.sonarsource.com/browse/SONAR-5891 and https://jira.sonarsource.com/browse/SONARMSBRU-285
            if (settings.ContainsKey("sonar.cs.msbuild.testProjectPattern"))
            {
                string value = settings["sonar.cs.msbuild.testProjectPattern"];
                if (value != oldDefaultProjectTestPattern)
                {
                    logger.LogWarning("The property 'sonar.cs.msbuild.testProjectPattern' defined in SonarQube is deprecated. Set the property 'sonar.msbuild.testProjectPattern' in the scanner instead.");
                }
                settings["sonar.msbuild.testProjectPattern"] = value;
                settings.Remove("sonar.cs.msbuild.testProjectPattern");
            }
            return settings;
        }

        private void GetPropertyValue(Dictionary<string, string> settings, JToken p)
        {
            string key = p["key"].ToString();
            if (p["value"] != null)
            {
                string value = p["value"].ToString();
                settings.Add(key, value);
            }
            else if (p["fieldValues"] != null)
            {
                MultivalueToProps(settings, key, (JArray)p["fieldValues"]);
            }
            else if (p["values"] != null)
            {
                JArray array = (JArray)p["values"];
                string value = string.Join(",", array.Values<string>());
                settings.Add(key, value);
            }
            else
            {
                throw new ArgumentException("Invalid property");
            }
        }

        private void MultivalueToProps(Dictionary<string, string> props, string settingKey, JArray array)
        {
            int id = 1;
            foreach (JObject obj in array.Children<JObject>())
            {
                foreach (JProperty prop in obj.Properties())
                {
                    string key = string.Concat(settingKey, ".", id, ".", prop.Name);
                    string value = prop.Value.ToString();
                    props.Add(key, value);
                }
                id++;
            }
        }

        private static string ParseRuleKey(string key)
        {
            int pos = key.IndexOf(':');
            return key.Substring(pos + 1);
        }

        /// <summary>
        /// Concatenates project key and branch into one string.
        /// </summary>
        /// <param name="projectKey">Unique project key</param>
        /// <param name="projectBranch">Specified branch of the project. Null if no branch to be specified.</param>
        /// <returns>A correctly formatted branch-specific identifier (if appropriate) for a given project.</returns>
        private static string GetProjectIdentifier(string projectKey, string projectBranch = null)
        {
            string projectId = projectKey;
            if (!string.IsNullOrWhiteSpace(projectBranch))
            {
                projectId = projectKey + ":" + projectBranch;
            }

            return projectId;
        }

        private string GetUrl(string format, params string[] args)
        {
            var queryString = EscapeQuery(format, args);
            if (!queryString.StartsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                queryString = string.Concat('/', queryString);
            }

            return this.serverUrl + queryString;
        }

        private string EscapeQuery(string format, params string[] args)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args.Select(a => WebUtility.UrlEncode(a)).ToArray());
        }

        #endregion

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Utilities.SafeDispose(this.downloader);
                }

                disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }

        #endregion

    }
}
