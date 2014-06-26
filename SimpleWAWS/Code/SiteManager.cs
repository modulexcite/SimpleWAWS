﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Kudu.Client.Editor;
using Kudu.Client.Zip;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.Management.WebSites;

namespace SimpleWAWS.Code
{
    public class SiteManager
    {
        private readonly SiteNameGenerator _nameGenerator = new SiteNameGenerator();
        public static TimeSpan SiteExpiryTime;
        private X509Certificate2 _cert;

        private readonly ConcurrentQueue<Site> _freeSites = new ConcurrentQueue<Site>();
        private readonly ConcurrentDictionary<string, Site> _sitesInUse = new ConcurrentDictionary<string, Site>();

        private Timer _timer;
        private readonly JobHost _jobHost = new JobHost();

        private static SiteManager _instance;
        public static async Task<SiteManager> GetInstanceAsync()
        {
            // TODO: what's the right way of locking when using async?
            if (_instance == null)
            {
                _instance = new SiteManager();
                await _instance.LoadSiteListFromAzureAsync();
            }

            return _instance;
        }

        private SiteManager()
        {
            string pfxPath = ConfigurationManager.AppSettings["pfxPath"];
            if (String.IsNullOrEmpty(pfxPath))
            {
                pfxPath = @"App_Data\cert.pfx";
            }

            pfxPath = Path.Combine(HttpRuntime.AppDomainAppPath, pfxPath);
            _cert = new X509Certificate2(
                pfxPath,
                ConfigurationManager.AppSettings["pfxPassword"],
                X509KeyStorageFlags.MachineKeySet);

            WebSpaceName = ConfigurationManager.AppSettings["webspace"];
            SiteExpiryTime = TimeSpan.FromMinutes(Int32.Parse(ConfigurationManager.AppSettings["siteExpiryMinutes"]));
        }

        private string WebSpaceName { get; set; }

        private async Task LoadSiteListFromAzureAsync()
        {
            List<WebSpace> webSpaces = GetAllWebSpaces().ToList();

            // Ask all webspaces to load their site lists (in parallel)
            await Task.WhenAll(webSpaces.Select(ws => ws.LoadAndCreateSitesAsync()));

            // Get a list of all the sites across all subscriptions/webspaces
            List<Site> allSites = webSpaces.SelectMany(ws => ws.Sites).ToList();

            // Check if the sites are in use and place them in the right list
            var tasksList = new List<Task>();
            foreach (Site site in allSites)
            {
                if (site.UserId != null)
                {
                    Trace.TraceInformation("Loading site {0} into the InUse list", site.Name);
                    if (!_sitesInUse.TryAdd(site.UserId, site))
                    {
                        Trace.TraceError("user {0} already had a site in the dictionary extra site is {1}. This shouldn't happen. Deleting and replacing the site.", site.UserId, site.Name);
                        tasksList.Add(site.DeleteAndCreateReplacementAsync());
                    }
                }
                else
                {
                    Trace.TraceInformation("Loading site {0} into the Free list", site.Name);
                    _freeSites.Enqueue(site);
                }
            }
            await Task.WhenAll(tasksList);
            // Do maintenance on the site lists every minute (and start one right now)
            _timer = new Timer(OnTimerElapsed);
            _timer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(60 * 1000));
        }

        private IEnumerable<WebSpace> GetAllWebSpaces()
        {
            string[] subscriptions = ConfigurationManager.AppSettings["subscriptions"].Split(',');
            string[] geoRegions = ConfigurationManager.AppSettings["geoRegions"].Split(',');

            foreach (string subscription in subscriptions)
            {
                var creds = new CertificateCloudCredentials(subscription, _cert);
                var client = new WebSiteManagementClient(creds);
                foreach (var geoRegion in geoRegions)
                {
                    yield return new WebSpace(this, client, geoRegion, _nameGenerator);
                }
            }
        }

        private async Task MaintainSiteLists()
        {
            await DeleteExpiredSitesAsync();
        }

        private void OnTimerElapsed(object state)
        {
            _jobHost.DoWork(() => { MaintainSiteLists().Wait(); });
        }

        public void OnSiteCreated(Site site)
        {
            _freeSites.Enqueue(site);
        }

        public void OnSiteDeleted(Site site)
        {
            if (site.UserId != null)
            {
                Site temp;
                _sitesInUse.TryRemove(site.UserId, out temp);
            }
            else
            {
                Trace.TraceWarning("site {0} was being deleted and it had a null UserId. Might point to an inconsistency in the data", site.Name);
            }
        }

        private async Task DeleteExpiredSitesAsync()
        {
            var siteIdsToDelete = new List<Site>();

            // Find all the expired sites
            foreach (var entry in _sitesInUse)
            {
                if (DateTime.UtcNow - entry.Value.StartTime > SiteExpiryTime)
                {
                    siteIdsToDelete.Add(entry.Value);
                }
            }
            var deleteTasks = new List<Task>();
            // Delete them
            foreach (var site in siteIdsToDelete)
            {
                Trace.TraceInformation("Deleting expired site {0}", site.Name);
                deleteTasks.Add(site.DeleteAndCreateReplacementAsync());
            }
            await Task.WhenAll(deleteTasks);
        }

        public async Task<Site> ActivateSiteAsync(Template template, string userId)
        {
            try
            {
                Site site;
                if (_freeSites.TryDequeue(out site))
                {
                    Trace.TraceInformation("Site {0} is now in use", site.Name);
                    if (template != null)
                    {
                        var credentials = new NetworkCredential(site.PublishingUserName, site.PublishingPassword);
                        var zipManager = new RemoteZipManager(site.ScmUrl + "zip/", credentials);
                        Task zipUpload = zipManager.PutZipFileAsync("site/wwwroot", template.GetFullPath());
                        var vfsManager = new RemoteVfsManager(site.ScmUrl + "vfs/", credentials);
                        Task deleteHostingStart = vfsManager.Delete("site/wwwroot/hostingstart.html");

                        await Task.WhenAll(zipUpload, deleteHostingStart);
                    }
                    if (!_sitesInUse.TryAdd(userId, site))
                    {
                        await site.DeleteAndCreateReplacementAsync();
                        throw new Exception("Can't have more than 1 free site at a time");
                    }
                    else
                    {
                        await site.MarkAsInUseAsync(userId);
                    }
                    return site;
                }
                else
                {
                    throw new Exception("No free sites are available, try again later");
                }
            }
            catch (InvalidOperationException ioe)
            {
                throw new Exception("No free sites are available, try again later", ioe);
            }
        }

        public Site GetSite(string userId)
        {
            Site site;
            _sitesInUse.TryGetValue(userId, out site);
            return site;
        }

        public async Task ResetAllFreeSites(string userId)
        {
            var list = new List<Site>();
            while (!_freeSites.IsEmpty)
            {
                Site temp;
                if (_freeSites.TryDequeue(out temp))
                {
                    list.Add(temp);
                }
            }
            await Task.WhenAll(list.Select(site =>
            {
                Trace.TraceInformation("Deleting site {0}", site.Name);
                return site.DeleteAndCreateReplacementAsync();
            }));
        }

        public async Task DeleteSite(string userId)
        {
            Site site;
            _sitesInUse.TryGetValue(userId, out site);

            if (site != null)
            {
                Trace.TraceInformation("Deleting site {0}", site.Name);
                await site.DeleteAndCreateReplacementAsync();
            }
        }

        public IEnumerable<Site> GetAllFreeSites()
        {
            return _freeSites.ToList();
        }

        public IEnumerable<Site> GetAllInUseSites()
        {
            return _sitesInUse.ToList().Select(s => s.Value);
        }
    }

}
