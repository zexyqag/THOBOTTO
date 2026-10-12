namespace THOBOTTO.Notifications;

public static class NotificationsStartup
{
    public static IServiceCollection AddNotifications(this IServiceCollection services) => services.AddSingleton<Notifier>();
}
