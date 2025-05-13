using System.Reflection;
using FakeItEasy;
using Meshmakers.Octo.Services.Infrastructure.Migrations;
using Xunit;

namespace Infrastructure.Tests.Tests;

public class MigrationCheckerTests
{

    [Fact]
    public void VerifyConsistency_SingleConfigType_Ok()
    {
        var loader = A.Fake<IMigrationLoader>();
        
        var a = new MigrationAttribute(0, 1, "Config1");
        var migrationTypeContext1 = new MigrationTypeContext(typeof(object), a);
        var b = new MigrationAttribute(1, 2, "Config1");
        var migrationTypeContext2 = new MigrationTypeContext(typeof(object), b);
        
        A.CallTo(() => loader.GetTypeContexts(A<Assembly>.Ignored))
            .Returns(new List<MigrationTypeContext> { migrationTypeContext1, migrationTypeContext2 });
        
        
        MigrationChecker.VerifyConsistency(loader, Assembly.GetExecutingAssembly());
    }
    
    [Fact]
    public void VerifyConsistency_MultipleConfigTypes_Ok()
    {
        var loader = A.Fake<IMigrationLoader>();
        
        var a = new MigrationAttribute(0, 1, "Config1");
        var migrationTypeContext1 = new MigrationTypeContext(typeof(object), a);
        var b = new MigrationAttribute(1, 2, "Config1");
        var migrationTypeContext2 = new MigrationTypeContext(typeof(object), b);
        
        var c = new MigrationAttribute(0, 1, "Config2");
        var migrationTypeContext3 = new MigrationTypeContext(typeof(object), c);
        var d = new MigrationAttribute(1, 2, "Config2");
        var migrationTypeContext4 = new MigrationTypeContext(typeof(object), d);
        
        A.CallTo(() => loader.GetTypeContexts(A<Assembly>.Ignored))
            .Returns(new List<MigrationTypeContext> { migrationTypeContext1, migrationTypeContext2, migrationTypeContext3, migrationTypeContext4 });
        
        
        MigrationChecker.VerifyConsistency(loader, Assembly.GetExecutingAssembly());
    }
    
    
    [Fact]
    public void VerifyConsistency_NoMigrationsFound_Throws()
    {
        var loader = A.Fake<IMigrationLoader>();
        A.CallTo(() => loader.GetTypeContexts(A<Assembly>.Ignored))
            .Returns(new List<MigrationTypeContext>());
        
        
        Assert.Throws<MigrationException>(() => MigrationChecker.VerifyConsistency(loader, Assembly.GetExecutingAssembly()));
    }

    [Fact]
    public void VerifyConsistency_NoInitialMigrationFound_Throws()
    {
        var a = new MigrationAttribute(2, 3, "Config1");
        var migrationTypeContext = new MigrationTypeContext(typeof(object), a);
        
        var loader = A.Fake<IMigrationLoader>();
        A.CallTo(() => loader.GetTypeContexts(A<Assembly>.Ignored))
            .Returns(new List<MigrationTypeContext> { migrationTypeContext });
        
        Assert.Throws<MigrationException>(() => MigrationChecker.VerifyConsistency(loader, Assembly.GetExecutingAssembly()));
    }
}