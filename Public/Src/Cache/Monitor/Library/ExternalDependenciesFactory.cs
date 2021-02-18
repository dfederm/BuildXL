﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.ContractsLight;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.Monitor.Library.Client;
using Kusto.Data;
using Kusto.Data.Net.Client;
using Kusto.Ingest;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.Monitor;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Rest.Azure.Authentication;

namespace BuildXL.Cache.Monitor.App
{
    internal static class ExternalDependenciesFactory
    {
        public static Result<IKustoClient> CreateKustoQueryClient(KustoCredentials credentials)
        {
            return CreateKustoQueryClient(credentials.ClusterUrl, credentials.Credentials.TenantId, credentials.Credentials.AppId, credentials.Credentials.AppKey);
        }

        public static Result<IKustoClient> CreateKustoQueryClient(string kustoClusterUrl, string azureTenantId, string azureAppId, string azureAppKey)
        {
            Contract.RequiresNotNullOrEmpty(kustoClusterUrl);
            Contract.RequiresNotNullOrEmpty(azureAppId);
            Contract.RequiresNotNullOrEmpty(azureAppKey);
            Contract.RequiresNotNullOrEmpty(azureTenantId);

            var kustoConnectionString = new KustoConnectionStringBuilder(kustoClusterUrl)
                .WithAadApplicationKeyAuthentication(azureAppId, azureAppKey, azureTenantId);
            return new Result<IKustoClient>(new KustoClient(KustoClientFactory.CreateCslQueryProvider(kustoConnectionString)));
        }

        public static Result<IKustoIngestClient> CreateKustoIngestClient(KustoCredentials credentials)
        {
            return CreateKustoIngestClient(credentials.ClusterUrl, credentials.Credentials.TenantId, credentials.Credentials.AppId, credentials.Credentials.AppKey);
        }

        public static Result<IKustoIngestClient> CreateKustoIngestClient(string kustoIngestionClusterUrl, string azureTenantId, string azureAppId, string azureAppKey)
        {
            Contract.RequiresNotNullOrEmpty(kustoIngestionClusterUrl);
            Contract.RequiresNotNullOrEmpty(azureTenantId);
            Contract.RequiresNotNullOrEmpty(azureAppId);
            Contract.RequiresNotNullOrEmpty(azureAppKey);

            var kustoIngestConnectionString = new KustoConnectionStringBuilder(kustoIngestionClusterUrl)
                .WithAadApplicationKeyAuthentication(azureAppId, azureAppKey, azureTenantId);
            return new Result<IKustoIngestClient>(KustoIngestFactory.CreateDirectIngestClient(kustoIngestConnectionString));
        }

        public static Task<Result<IMonitorManagementClient>> CreateAzureMetricsClientAsync(AzureCredentials credentials)
        {
            return CreateAzureMetricsClientAsync(credentials.Credentials.TenantId, credentials.SubscriptionId, credentials.Credentials.AppId, credentials.Credentials.AppKey);
        }

        public static async Task<Result<IMonitorManagementClient>> CreateAzureMetricsClientAsync(string azureTenantId, string azureSubscriptionId, string azureAppId, string azureAppKey)
        {
            var credentials = await ApplicationTokenProvider.LoginSilentAsync(azureTenantId, azureAppId, azureAppKey);

            return new MonitorManagementClient(credentials)
            {
                SubscriptionId = azureSubscriptionId
            };
        }

        public static Result<IAzure> CreateAzureClient(AzureCredentials credentials)
        {
            return CreateAzureClient(credentials.Credentials.TenantId, credentials.SubscriptionId, credentials.Credentials.AppId, credentials.Credentials.AppKey);
        }

        public static Result<IAzure> CreateAzureClient(string azureTenantId, string azureSubscriptionId, string azureAppId, string azureAppKey)
        {
            var tokenCredentials = SdkContext.AzureCredentialsFactory.FromServicePrincipal(azureAppId, azureAppKey, azureTenantId, AzureEnvironment.AzureGlobalCloud);

            return new Result<IAzure>(Microsoft.Azure.Management.Fluent.Azure
                .Configure()
                .WithLogLevel(HttpLoggingDelegatingHandler.Level.BodyAndHeaders)
                .Authenticate(tokenCredentials).WithSubscription(azureSubscriptionId));
        }
    }
}
