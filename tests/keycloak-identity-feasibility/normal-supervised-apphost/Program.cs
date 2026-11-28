using System.Collections.Immutable;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;
using ForgeTrust.AppSurface.Auth.Keycloak;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if(args is not [var root,var scenario])return 64;
var input=JsonSerializer.Deserialize<Input>(File.ReadAllBytes(Path.Join(root,"input.json")))!;
var projectDirectory = input.ProjectDirectory is null ? root : Path.GetFullPath(input.ProjectDirectory);
AppSurfaceKeycloakThemeOptions? AcceptanceTheme() => input.ThemeDirectory is null ? null :
    new AppSurfaceKeycloakThemeOptions("acceptance", input.ThemeDirectory,
        AppSurfaceKeycloakImageReference.Parse(input.ThemeImageReference
            ?? throw new InvalidDataException("A pinned theme image reference is required."))) { Platform = "linux/arm64" };
var execution=new AppSurfaceKeycloakSupervisedOptions{LeaseRoot=input.LeaseRoot,RunRoot=Path.Join(Directory.GetParent(root)!.FullName,"runs")};
if (Environment.GetEnvironmentVariable("DOCKER_HOST") is { } dockerEndpoint)
    execution.DockerEndpoint = dockerEndpoint;
if(scenario.StartsWith("recover-",StringComparison.Ordinal))
{
    try
    {
        var recovery = scenario is "recover-marker-bytes" or "recover-marker-replaced"
            ? await AppSurfaceKeycloakSupervisedRun.VerifyStoppedAndClearAsync(execution, input.VolumeName, input.ExpectedOwner!, async ct =>
            {
                File.WriteAllText(Path.Join(root, "recovery.preclear.ready"), "stopped-verified");
                while (!File.Exists(Path.Join(root, "recovery.preclear.release"))) await Task.Delay(20, ct);
            })
            : await AppSurfaceKeycloakSupervisedRun.VerifyStoppedAndClearAsync(execution, input.VolumeName, input.ExpectedOwner!);
        File.WriteAllText(Path.Join(root,"recovery.json"),JsonSerializer.Serialize(recovery));return 0;
    }
    catch (Exception error)
    {
        var code = error is QualifiedOwnershipFailure failure ? failure.Kind : "ASKEYC033";
        File.WriteAllText(Path.Join(root, "recovery-rejection.json"), JsonSerializer.Serialize(new { Code = code }));
        System.Console.Error.WriteLine("Recovery rejected. Code: ASKEYC033. workloads_started=false");
        return 33;
    }
}
if (scenario == "legacy-name")
{
    var probeBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
    {
        Args = [],
        DisableDashboard = true,
        ProjectDirectory = projectDirectory,
    });
    var historical = probeBuilder.AddAppSurfaceKeycloak("identity", options =>
    {
        options.KeycloakPort = input.KeycloakPort;
        options.WebProofPort = input.WebPort;
        options.UsePersistentDataVolume = true;
        options.RealmImportDirectory = Path.Join(root, "realm-import");
        options.LoginTheme = AcceptanceTheme();
    });
    var dataMount = historical.Resource.Resource.Annotations
        .OfType<ContainerMountAnnotation>()
        .Single(mount => mount.Target == "/opt/keycloak/data");
    var actualName = dataMount.Source ?? throw new InvalidDataException("Aspire generated an anonymous Keycloak data volume.");
    File.WriteAllText(Path.Join(root, "legacy-name.json"), JsonSerializer.Serialize(new
    {
        volume = actualName,
        importPath = historical.RealmImportFile,
    }));
    return 0;
}
var legacy = scenario.StartsWith("legacy-", StringComparison.Ordinal);
var neutral = scenario.StartsWith("neutral-", StringComparison.Ordinal) || legacy;
var skoolit = scenario.StartsWith("skoolit-", StringComparison.Ordinal);
var acceptance = neutral || skoolit;
var google = scenario is "skoolit-google" or "skoolit-google-id-only";
var postSeedProof = acceptance || scenario == "post-seed-clean" || scenario.StartsWith("drift-", StringComparison.Ordinal);
var builder=DistributedApplication.CreateBuilder(new DistributedApplicationOptions{Args=[],DisableDashboard=true,ProjectDirectory=projectDirectory});
builder.Services.AddSingleton<IHostLifetime,ControlledLifetime>();
var user=builder.AddParameter("local-admin-user",()=>"proof-admin");
var pass=builder.AddParameter("local-admin-password",()=>File.ReadAllText(Path.Join(root,"private/admin")),secret:true);
var client = neutral ? null : builder.AddParameter("local-client", () => File.ReadAllText(Path.Join(root, "private/client")), secret: true);
var seedPassword=builder.AddParameter("seed-password",()=>File.ReadAllText(Path.Join(root,"private/user")),secret:true);
var credentials = new AppSurfaceKeycloakCredentialBindings { ClientSecret = client };
if ((postSeedProof && !acceptance) || scenario is "skoolit-google" or "skoolit-google-secret-only")
    credentials.IdentityProviderSecrets.Add("google", builder.AddParameter("proof-broker", () => File.ReadAllText(Path.Join(root, "private/broker")), secret: true));
AppSurfaceKeycloakResource wrapper;
try
{
    wrapper = builder.AddAppSurfaceKeycloak("identity", user, pass, credentials, options =>
    {
        options.Realm = neutral ? "appsurface-dev" : skoolit ? "skoolit" : "sample";
        options.ClientId = neutral ? "appsurface-web" : skoolit ? "skoolit-web" : "sample-web";
        options.CallbackPath = neutral ? "/signin-appsurface-oidc" : "/signin-oidc";
        options.SignedOutCallbackPath = neutral ? "/signout-callback-appsurface-oidc" : "/signout-callback-oidc";
        options.KeycloakPort = input.KeycloakPort;
        options.WebProofPort = input.WebPort;
        options.ClientMode = neutral ? KeycloakClientMode.Public : KeycloakClientMode.Confidential;
        var usesLegacyGeneratedName = scenario is "legacy-repeat" or "legacy-missing" or "legacy-moved";
        options.PersistentVolumeName = usesLegacyGeneratedName ? null : input.VolumeName;
        options.UsePersistentDataVolume = usesLegacyGeneratedName;
        options.RequireIdentitySeed = true;
        options.SeededUsers.Clear();
        options.SupervisedExecution = execution;
        if (scenario == "skoolit-tunnel")
            options.Authority = new Uri("https://auth-dev.getskoolit.com/realms/skoolit");
        if (scenario == "skoolit-unapproved-issuer")
            options.Authority = new Uri("https://unapproved.getskoolit.com/realms/skoolit");
        if (acceptance)
        {
            int[] ports = skoolit ? [input.WebPort, 5000, 5055] : [input.WebPort];
            var origins = (from port in ports.Distinct()
                           from host in new[] { "localhost", "127.0.0.1" }
                           select new Uri($"http://{host}:{port}")).ToList();
            if (skoolit)
            {
                var publicWeb = new Uri("https://app-dev.getskoolit.com");
                options.ApprovedHttpsOrigins.Add(publicWeb);
                options.ApprovedHttpsOrigins.Add(new Uri("https://auth-dev.getskoolit.com"));
                origins.Add(publicWeb);
            }
            foreach (var origin in origins)
            {
                options.WebOrigins.Add(origin);
                options.RedirectUris.Add(new Uri(origin, options.CallbackPath));
                options.PostLogoutRedirectUris.Add(new Uri(origin, options.SignedOutCallbackPath));
            }
            options.LoginTheme = AcceptanceTheme();
        }
        if (skoolit)
        {
            options.Mappers.Add(new("skoolit app user id", "appsurface.subject_id", UserAttribute: "skoolit_app_user_id"));
            options.Mappers.Add(new("skoolit identity provider alias", "identity_provider", SessionNote: "identity_provider"));
            if (scenario == "skoolit-google-removed") options.RemovedIdentityProviderAliases.Add("google");
        }
        else if (!neutral) options.Mappers.Add(new("managed-role", "fixture_role", "viewer"));
        if (google || (postSeedProof && !acceptance)) options.IdentityProviders.Add(new("google", "synthetic-proof-client"));
    });
}
catch (AppSurfaceKeycloakException error) when (
    acceptance && error.Code == "ASKEYC027"
    || scenario == "skoolit-unapproved-issuer" && error.Code == "ASKEYC001")
{
    File.WriteAllText(Path.Join(root, "registration-rejection.json"), JsonSerializer.Serialize(new { error.Code }));
    return 44;
}
var run=wrapper.SupervisedRun!;
run.LifecycleObservation=async(phase,ct)=>
{
    File.WriteAllText(Path.Join(root,"lifecycle-"+phase+".json"),run.RunDirectory);
    if (postSeedProof && phase is "worker-completed" or "seeds-completed")
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var release = Path.Join(root, phase + ".release");
        while (!File.Exists(release)) await Task.Delay(20, deadline.Token);
        if (File.ReadAllText(release) != "unchanged") throw new InvalidDataException("Managed-state acceptance rejected.");
    }
    var target=scenario.StartsWith("loss-",StringComparison.Ordinal)?scenario[5..]:scenario.StartsWith("cancel-",StringComparison.Ordinal)?scenario[7..]:null;
    if(target!=phase)return;
    File.WriteAllText(Path.Join(root,"phase.ready"),phase);
    while(!File.Exists(Path.Join(root,"phase.release")))await Task.Delay(20,ct);
};
var publicInputs = new Dictionary<string, string>
{
    ["SeedMode"] = scenario is "seed-failure" or "skoolit-seed-failure" ? "fail"
        : scenario.StartsWith("drift-", StringComparison.Ordinal) ? scenario : "success",
    ["DeclaredPublic"] = scenario == "public-secret" ? File.ReadAllText(Path.Join(root, "private/client")) : "safe",
};
if (scenario == "skoolit-tunnel") publicInputs["PublicWebOrigin"] = "https://app-dev.getskoolit.com";
run.WithPublicInputs(publicInputs);
var probeSeed=scenario.EndsWith("required-seed-started",StringComparison.Ordinal)||scenario=="launch-failure";
run.WithSeed(new("required-seed",input.Payload,probeSeed?"LifecycleProbe.dll":"SupervisedSeed.dll",probeSeed?[scenario=="launch-failure"?"fail":"hold"]:["/proof/public/config.json","/proof/private/input.json","/proof/state/seed-result.json"])
{PrivateInputs=new Dictionary<string,Aspire.Hosting.ApplicationModel.IResourceBuilder<Aspire.Hosting.ApplicationModel.ParameterResource>>
    {{"AdminUsername",user},{"AdminPassword",pass},{"UserPassword",seedPassword}}.ToImmutableDictionary()});
run.WithWeb(new("real-web",input.Payload,"SupervisedWeb.dll",["/proof/public/config.json"]));
run.InjectUnverifiedStop=scenario=="uncertain-stop";
run.BeforeCleanObservation=async ct=>
{
    File.WriteAllText(Path.Join(root,"preclean.ready"),run.RunDirectory);
    while(!File.Exists(Path.Join(root,"preclean.release")))await Task.Delay(30,ct);
};
File.WriteAllText(Path.Join(root,"registration.json"),JsonSerializer.Serialize(new{run.OwnerId,run.RunDirectory,staged=Directory.Exists(run.RunDirectory),api="AddAppSurfaceKeycloak"}));
var app=builder.Build();
using var cancel=new CancellationTokenSource();
using var sig=System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM,c=>{c.Cancel=true;cancel.Cancel();});
var finish=run.RunAsync(app,cancel.Token);
while(!finish.IsCompleted)
{
    if(File.Exists(Path.Join(root,"stop.request"))){cancel.Cancel();break;}
    await Task.Delay(50);
}
var result=await finish;
File.WriteAllText(Path.Join(root,"host-result.json"),JsonSerializer.Serialize(result));
return result.ExitCode;

sealed record Input(string LeaseRoot,string VolumeName,int KeycloakPort,int WebPort,string Payload,string? ExpectedOwner,
    string? ProjectDirectory = null,string? ThemeDirectory = null,string? ThemeImageReference = null);
sealed class ControlledLifetime:IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken)=>Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken)=>Task.CompletedTask;
}
