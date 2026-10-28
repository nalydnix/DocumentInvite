using DocumentInvite.Api.Common;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Demo;
using DocumentInvite.Api.Features.Files;
using DocumentInvite.Api.Features.Gdpr;
using DocumentInvite.Api.Features.Invites;
using DocumentInvite.Api.Features.Loans;
using DocumentInvite.Api.Features.Messaging;
using DocumentInvite.Api.Features.Portal;
using DocumentInvite.Api.Features.Templates;
using DocumentInvite.Api.Features.Outbox;
using DocumentInvite.Api.Features.Reminders;
using DocumentInvite.Api.Features.Review;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddPlatform();
builder.Services.AddStaffAuth();
builder.Services.AddTemplates();
builder.Services.AddLoans();
builder.Services.AddInvites();
builder.Services.AddEmail();
builder.Services.AddReminders();
builder.Services.AddFiles();
builder.Services.AddPortal();
builder.Services.AddReview();
builder.Services.AddDemo();
builder.Services.AddMessaging();
builder.Services.AddGdpr();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UsePlatform();
app.UseStaffAuth();
app.UsePortal();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapStaffAuth();
app.MapControllers();

app.Run();

public partial class Program;
