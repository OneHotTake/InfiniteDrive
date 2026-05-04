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
    public class AnimePluginStatusRequest : IReturn<AnimePluginStatusResponse> { }

    /// <summary>Response indicating anime plugin installation status.</summary>
    public class AnimePluginStatusResponse
    {
        /// <summary><c>true</c> if the Emby Anime Plugin is detected.</summary>
        public bool Installed { get; set; }
    }

    /// <summary>
    /// Service for checking anime plugin installation status.
    /// </summary>
    public class AnimePluginStatusService : IService
    {
        public object Get(AnimePluginStatusRequest _)
        {
            return new AnimePluginStatusResponse
            {
                Installed = Plugin.IsAnimePluginInstalled()
            };
        }
    }
}
