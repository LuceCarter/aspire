// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class AzureRedisExtensionsTests(ITestOutputHelper output)
{
    /// <summary>
    /// Test both with and without ACA infrastructure because the role assignments
    /// are handled differently between the two. This ensures that the bicep is generated
    /// consistently regardless of the infrastructure used in RunMode.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAzureRedis(bool useAcaInfrastructure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        if (useAcaInfrastructure)
        {
            builder.AddAzureContainerAppsInfrastructure();
        }

        var redis = builder.AddAzureRedis("redis-cache");

        builder.AddContainer("api", "myimage")
            .WithReference(redis);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        var manifest = await AzureManifestUtils.GetManifestWithBicep(redis.Resource, skipPreparer: true);

        var expectedBicep = """
            @description('The location for the resource(s) to be deployed.')
            param location string = resourceGroup().location

            param principalId string

            param principalName string

            resource redis_cache 'Microsoft.Cache/redis@2024-03-01' = {
              name: take('rediscache-${uniqueString(resourceGroup().id)}', 63)
              location: location
              properties: {
                sku: {
                  name: 'Basic'
                  family: 'C'
                  capacity: 1
                }
                enableNonSslPort: false
                disableAccessKeyAuthentication: true
                minimumTlsVersion: '1.2'
                redisConfiguration: {
                  'aad-enabled': 'true'
                }
              }
              tags: {
                'aspire-resource-name': 'redis-cache'
              }
            }

            resource redis_cache_contributor 'Microsoft.Cache/redis/accessPolicyAssignments@2024-03-01' = {
              name: guid(redis_cache.id, principalId, 'Data Contributor')
              properties: {
                accessPolicyName: 'Data Contributor'
                objectId: principalId
                objectIdAlias: principalName
              }
              parent: redis_cache
            }

            output connectionString string = '${redis_cache.properties.hostName},ssl=true'

            output name string = redis_cache.name
            """;
        output.WriteLine(manifest.BicepText);
        Assert.Equal(expectedBicep, manifest.BicepText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mykeyvault")]
    public async Task AddAzureRedisWithAccessKeyAuthentication(string? kvName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var redis = builder.AddAzureRedis("redis-cache");

        if (kvName is null)
        {
            kvName = "redis-cache-kv";

            redis.WithAccessKeyAuthentication();
        }
        else
        {
            redis.WithAccessKeyAuthentication(builder.AddAzureKeyVault(kvName));
        }

        var manifest = await AzureManifestUtils.GetManifestWithBicep(redis.Resource);

        var expectedManifest = $$"""
            {
              "type": "azure.bicep.v0",
              "connectionString": "{{{kvName}}.secrets.connectionstrings--redis-cache}",
              "path": "redis-cache.module.bicep",
              "params": {
                "keyVaultName": "{{{kvName}}.outputs.name}"
              }
            }
            """;
        var m = manifest.ManifestNode.ToString();
        output.WriteLine(m);
        Assert.Equal(expectedManifest, m);

        var expectedBicep = """
            @description('The location for the resource(s) to be deployed.')
            param location string = resourceGroup().location
            
            param keyVaultName string
            
            resource redis_cache 'Microsoft.Cache/redis@2024-03-01' = {
              name: take('rediscache-${uniqueString(resourceGroup().id)}', 63)
              location: location
              properties: {
                sku: {
                  name: 'Basic'
                  family: 'C'
                  capacity: 1
                }
                enableNonSslPort: false
                minimumTlsVersion: '1.2'
              }
              tags: {
                'aspire-resource-name': 'redis-cache'
              }
            }
            
            resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
              name: keyVaultName
            }
            
            resource connectionString 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
              name: 'connectionstrings--redis-cache'
              properties: {
                value: '${redis_cache.properties.hostName},ssl=true,password=${redis_cache.listKeys().primaryKey}'
              }
              parent: keyVault
            }
            
            output name string = redis_cache.name
            """;
        output.WriteLine(manifest.BicepText);
        Assert.Equal(expectedBicep, manifest.BicepText);
    }

    [Fact]
    public async Task AddAzureRedisRunAsContainerProducesCorrectConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        RedisResource? redisResource = null;
        var redis = builder.AddAzureRedis("cache")
            .RunAsContainer(c =>
            {
                redisResource = c.Resource;

                c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 12455));
            });

        Assert.True(redis.Resource.IsContainer(), "The resource should now be a container resource.");

        Assert.NotNull(redisResource?.PasswordParameter);
        Assert.Equal($"localhost:12455,password={redisResource.PasswordParameter.Value}", await redis.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunAsContainerAppliesAnnotationsCorrectly(bool before)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var cache = builder.AddAzureRedis("cache");

        if (before)
        {
            cache.WithAnnotation(new Dummy1Annotation());
        }

        cache.RunAsContainer(c =>
        {
            c.WithAnnotation(new Dummy2Annotation());
        });

        if (!before)
        {
            cache.WithAnnotation(new Dummy1Annotation());
        }

        var cacheInModel = builder.Resources.Single(r => r.Name == "cache");

        Assert.True(cacheInModel.TryGetAnnotationsOfType<Dummy1Annotation>(out var cacheAnnotations1));
        Assert.Single(cacheAnnotations1);

        Assert.True(cacheInModel.TryGetAnnotationsOfType<Dummy2Annotation>(out var cacheAnnotations2));
        Assert.Single(cacheAnnotations2);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);

    private sealed class Dummy1Annotation : IResourceAnnotation
    {
    }

    private sealed class Dummy2Annotation : IResourceAnnotation
    {
    }
}
