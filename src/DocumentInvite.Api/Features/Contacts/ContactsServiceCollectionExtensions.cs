namespace DocumentInvite.Api.Features.Contacts;

public static class ContactsServiceCollectionExtensions
{
    public static IServiceCollection AddContacts(this IServiceCollection services) =>
        services.AddScoped<ContactService>();
}
