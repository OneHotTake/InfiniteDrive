using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Notifications;
using Microsoft.Extensions.Logging;

namespace InfiniteDrive.Services
{
    /// <summary>
    /// Sends Emby notifications for InfiniteDrive pipeline events.
    /// Uses <see cref="INotificationManager"/> for native dashboard notifications.
    /// </summary>
    internal static class NotificationService
    {
        private static INotificationManager? _manager;

        public static void Initialize(INotificationManager manager)
        {
            _manager = manager;
        }

        /// <summary>Notify all admins about a pipeline event.</summary>
        public static async Task NotifyAsync(
            string notificationType,
            string name,
            string description,
            NotificationLevel level = NotificationLevel.Normal,
            CancellationToken cancellationToken = default)
        {
            if (_manager == null) return;

            try
            {
                var request = new MediaBrowser.Model.Notifications.NotificationRequest
                {
                    NotificationType = notificationType,
                    Name = name,
                    Description = description,
                    Level = level,
                    Date = DateTimeOffset.UtcNow,
                    SendToUserMode = SendToUserType.Admins,
                };

                await _manager.SendNotification(request, cancellationToken);
            }
            catch (Exception ex)
            {
                // Notifications are best-effort — never block the pipeline
                System.Console.WriteLine($"[InfiniteDrive] Notification failed: {ex.Message}");
            }
        }
    }
}
