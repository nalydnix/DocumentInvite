using DocumentInvite.Api.Common;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddPlatform();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UsePlatform();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapControllers();

app.Run();

public partial class Program;
