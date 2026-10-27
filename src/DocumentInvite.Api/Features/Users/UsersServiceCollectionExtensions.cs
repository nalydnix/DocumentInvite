namespace DocumentInvite.Api.Features.Users;

public static class UsersServiceCollectionExtensions
{
    public static IServiceCollection AddUserAdmin(this IServiceCollection services) =>
        services.AddScoped<UserAdminService>();
}
