using ApplicationBuilderHelpers;
using Infrastructure.SQLite;
using Microsoft.Extensions.Hosting;
using Presentation.Client;

var builder = ApplicationDependencyBuilder.FromBuilder(Host.CreateApplicationBuilder(args));

builder.Add<PresentationClient>();
builder.Add<SQLiteApplication>();

builder.Run();
