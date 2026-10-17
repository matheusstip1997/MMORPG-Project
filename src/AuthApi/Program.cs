// Auth API (spec 2.1, 2.5, 9.10): login, character list, create, soft delete, and "enter world" tickets.
// Settings come from environment variables (AuthApiSettings); run command and dev values in CLAUDE.md.

using AuthApi;

var app = AuthApiApp.Build(args);
app.Run();

/// <summary>Public so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
