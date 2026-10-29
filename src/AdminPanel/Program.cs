// Admin panel v0 (spec 7.4, backlog J): search characters, kick, view logs and telemetry. Dev only in M1.
// Settings come from environment variables (AdminPanelSettings); README.md in this folder has the dev setup.

using AdminPanel;

var app = AdminPanelApp.Build(args);
app.Run();

/// <summary>Public so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
