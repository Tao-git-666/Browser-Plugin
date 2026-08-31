// This fixture intentionally defines the small SDK surface it needs, so the
// decompiler integration test does not require a real Dynamics SDK package.
namespace Microsoft.Xrm.Sdk
{

public interface IPlugin
{
    void Execute(IServiceProvider serviceProvider);
}

public interface IPluginExecutionContext
{
    string MessageName { get; }

    string PrimaryEntityName { get; }

    ParameterCollection InputParameters { get; }
}

public interface IOrganizationServiceFactory
{
    IOrganizationService CreateOrganizationService(Guid? userId);
}

public interface IOrganizationService
{
    void Update(Entity entity);
}

public sealed class ParameterCollection : Dictionary<string, object>
{
    public bool Contains(string key) => ContainsKey(key);
}

public sealed class Entity(string logicalName)
{
    public string LogicalName { get; } = logicalName;

    public Dictionary<string, object> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public object this[string name]
    {
        get => Attributes[name];
        set => Attributes[name] = value;
    }
}

public sealed class InvalidPluginExecutionException(string message) : Exception(message);
}

namespace Contoso.Crm.Plugins
{

using Microsoft.Xrm.Sdk;

public sealed class AccountApprovalPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext))!;
        var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory))!;
        IOrganizationService service = factory.CreateOrganizationService(null);

        if (context.MessageName != "Update" || context.PrimaryEntityName != "account")
        {
            return;
        }

        if (!context.InputParameters.Contains("Target"))
        {
            throw new InvalidPluginExecutionException("缺少 Target，无法执行客户审批逻辑");
        }

        Entity account = new Entity("account");
        account["new_approved"] = true;
        account["statuscode"] = 2;
        service.Update(account);
    }
}
}
