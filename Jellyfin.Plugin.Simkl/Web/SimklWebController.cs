using System;
using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Simkl.Web
{
    /// <summary>
    /// Web controller for serving user configuration pages.
    /// </summary>
    [ApiController]
    [Route("SimklWeb")]
    public class SimklWebController : ControllerBase
    {
        /// <summary>
        /// Test endpoint to check if routing works.
        /// </summary>
        /// <returns>Simple test response.</returns>
        [HttpGet("test")]
        public IActionResult TestEndpoint()
        {
            return Ok(new { message = "Simkl web controller is working!", timestamp = DateTime.Now });
        }

        /// <summary>
        /// Serves the user configuration page.
        /// </summary>
        /// <returns>The user configuration HTML page.</returns>
        [HttpGet("settings")]
        [Authorize]
        public IActionResult GetUserSettingsPage()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Jellyfin.Plugin.Simkl.Configuration.userConfigPage.html";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return NotFound("User configuration page not found");
            }

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            return Content(content, "text/html");
        }
    }
}