using DocumentInvite.Api.Common;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Outbox;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddPlatform();
builder.Services.AddStaffAuth();
builder.Services.AddEmail();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UsePlatform();
app.UseStaffAuth();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapStaffAuth();
app.MapControllers();

app.Run();

public partial class Program;
