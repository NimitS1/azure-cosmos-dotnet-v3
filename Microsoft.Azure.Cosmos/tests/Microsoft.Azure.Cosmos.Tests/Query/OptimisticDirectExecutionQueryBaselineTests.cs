﻿namespace Microsoft.Azure.Cosmos.Tests.Query
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Xml;
    using Microsoft.Azure.Cosmos.CosmosElements;
    using Microsoft.Azure.Cosmos.Pagination;
    using Microsoft.Azure.Cosmos.Query;
    using Microsoft.Azure.Cosmos.Query.Core;
    using Microsoft.Azure.Cosmos.Query.Core.Exceptions;
    using Microsoft.Azure.Cosmos.Query.Core.ExecutionContext;
    using Microsoft.Azure.Cosmos.Query.Core.Monads;
    using Microsoft.Azure.Cosmos.Query.Core.Pipeline;
    using Microsoft.Azure.Cosmos.Query.Core.Pipeline.CrossPartition.OrderBy;
    using Microsoft.Azure.Cosmos.Query.Core.Pipeline.Distinct;
    using Microsoft.Azure.Cosmos.Query.Core.Pipeline.OptimisticDirectExecutionQuery;
    using Microsoft.Azure.Cosmos.Query.Core.Pipeline.Pagination;
    using Microsoft.Azure.Cosmos.Query.Core.QueryClient;
    using Microsoft.Azure.Cosmos.Query.Core.QueryPlan;
    using Microsoft.Azure.Cosmos.Test.BaselineTest;
    using Microsoft.Azure.Cosmos.Tests.Pagination;
    using Microsoft.Azure.Cosmos.Tracing;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Moq;
    using Newtonsoft.Json;
    using static Microsoft.Azure.Cosmos.Query.Core.ExecutionContext.CosmosQueryExecutionContextFactory;

    [TestClass]
    public class OptimisticDirectExecutionQueryBaselineTests : BaselineTests<OptimisticDirectExecutionTestInput, OptimisticDirectExecutionTestOutput>
    {
        [TestMethod]
        [Owner("akotalwar")]
        public void PositiveOptimisticDirectExecutionOutput()
        {
            CosmosElement cosmosElementContinuationToken = CosmosElement.Parse(
                "{\"OptimisticDirectExecutionToken\":{\"token\":\"{\\\"resourceId\\\":\\\"AQAAAMmFOw8LAAAAAAAAAA==\\\",\\\"skipCount\\\":1}\"," +
                "\"range\":{\"min\":\"\",\"max\":\"FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF\"}}}");

            List<OptimisticDirectExecutionTestInput> testVariations = new List<OptimisticDirectExecutionTestInput>
            {
                CreateInput(
                    description: @"Single Partition Key and Distinct",
                    query: "SELECT DISTINCT c.age FROM c",
                    expectedOptimisticDirectExecution: true,
                    partitionKeyPath: @"/pk",
                    partitionKeyValue: @"value"),

                CreateInput(
                    description: @"Single Partition Key and Min Aggregate",
                    query: "SELECT VALUE MIN(c.age) FROM c",
                    expectedOptimisticDirectExecution: true,
                    partitionKeyPath: @"/pk",
                    partitionKeyValue: @"value"),

                CreateInput(
                    description: @"Single Partition Key and Value Field",
                    query: "SELECT c.age FROM c",
                    expectedOptimisticDirectExecution: true,
                    partitionKeyPath: @"/pk",
                    partitionKeyValue: @"value"),

                CreateInput(
                    description: @"Single Partition Key and Value Field",
                    query: "SELECT * FROM c",
                    expectedOptimisticDirectExecution: true,
                    partitionKeyPath: @"/pk",
                    partitionKeyValue: "a",
                    continuationToken: null),

                CreateInput(
                    description: @"Single Partition Key and continuation token",
                    query: "SELECT * FROM c",
                    expectedOptimisticDirectExecution: true,
                    partitionKeyPath: @"/pk",
                    partitionKeyValue: "a",
                    continuationToken: cosmosElementContinuationToken),
            };
            this.ExecuteTestSuite(testVariations);
        }

        [TestMethod]
        [Owner("akotalwar")]
        public void NegativeOptimisticDirectExecutionOutput()
        {
            List<OptimisticDirectExecutionTestInput> testVariations = new List<OptimisticDirectExecutionTestInput>
            {
                CreateInput(
                    description: @"Null Partition Key Value",
                    query: "SELECT * FROM c",
                    expectedOptimisticDirectExecution: false,
                    partitionKeyPath: @"/pk",
                    partitionKeyValue: Cosmos.PartitionKey.Null),

                CreateInput(
                    description: @"None Partition Key Value",
                    query: "SELECT * FROM c",
                    expectedOptimisticDirectExecution: false,
                    partitionKeyPath: @"/pk",
                    partitionKeyValue: Cosmos.PartitionKey.None),

                CreateInput(
                    description: @"C# Null Partition Key Value",
                    query: "SELECT * FROM c",
                    expectedOptimisticDirectExecution: false,
                    partitionKeyPath: @"/pk",
                    partitionKeyValue: null),
               
                // Fail to execute due to MalFormedContinuationToken Exception
                CreateInput(
                    description: @"Exception with malformed continuation token",
                    query: "SELECT * FROM c",
                    expectedOptimisticDirectExecution: false,
                    partitionKeyPath: @"/pk",
                    partitionKeyValue: "a",
                    continuationToken: CosmosString.Create("asdf"),
                    expectException: true),
            };
            this.ExecuteTestSuite(testVariations);
        }
        
        // This test confirms that TestInjection.EnableOptimisticDirectExection is set to false from default. 
        // Check test "TestPipelineForDistributedQueryAsync" to understand why this is done
        [TestMethod]
        public async Task TestDefaultTestInjectionSettingsAsync()
        {
            TestInjections testInjection = new TestInjections(simulate429s: false, simulateEmptyPages: false);

            Assert.AreEqual(testInjection.EnableOptimisticDirectExecution, false);
        }

        // test checks that the pipeline can take a query to the backend and returns its associated document(s).
        [TestMethod]
        public async Task TestPipelineForBackendDocumentsOnSinglePartitionAsync()
        {
            int numItems = 100;
            OptimisticDirectExecutionTestInput input = CreateInput(
                    description: @"Single Partition Key and Value Field",
                    query: "SELECT VALUE COUNT(1) FROM c",
                    expectedOptimisticDirectExecution: true,
                    partitionKeyPath: @"/pk",
                    partitionKeyValue: "a");

            QueryRequestOptions queryRequestOptions = GetQueryRequestOptions(enableOptimisticDirectExecution: true);

            DocumentContainer inMemoryCollection = await CreateDocumentContainerAsync(numItems, multiPartition: false);
            IQueryPipelineStage queryPipelineStage = await GetOdePipelineAsync(input, inMemoryCollection, queryRequestOptions);
            int documentCountInSinglePartition = 0;

            while (await queryPipelineStage.MoveNextAsync(NoOpTrace.Singleton))
            {
                Assert.AreEqual(TestInjections.PipelineType.OptimisticDirectExecution, queryRequestOptions.TestSettings.Stats.PipelineType.Value);

                TryCatch<QueryPage> tryGetPage = queryPipelineStage.Current;
                tryGetPage.ThrowIfFailed();

                documentCountInSinglePartition += Int32.Parse(tryGetPage.Result.Documents[0].ToString());

                if (tryGetPage.Result.State == null)
                {
                    break;
                }
            }

            Assert.AreEqual(100, documentCountInSinglePartition);
        }

        // test checks that the pipeline can take a query to the backend and returns its associated document(s) + continuation token.
        [TestMethod]
        public async Task TestPipelineForContinuationTokenOnSinglePartitionAsync()
        {
            int numItems = 100;
            OptimisticDirectExecutionTestInput input = CreateInput(
                    description: @"Single Partition Key and Value Field",
                    query: "SELECT * FROM c",
                    expectedOptimisticDirectExecution: true,
                    partitionKeyPath: @"/pk",
                    partitionKeyValue: "a");

            int result = await this.GetPipelineAndDrainAsync(
                            input,
                            numItems: numItems,
                            isMultiPartition: false,
                            expectedContinuationTokenCount: 10);

            Assert.AreEqual(numItems, result);
        }

        // test to check if pipeline handles a 410 exception properly and returns all the documents.
        [TestMethod]
        public async Task TestPipelineForGoneExceptionOnSingleAndMultiplePartitionAsync()
        {
            Assert.IsTrue(await ExecuteGoneExceptionOnODEPipeline(isMultiPartition: false));

            Assert.IsTrue(await ExecuteGoneExceptionOnODEPipeline(isMultiPartition: true));
        }

        // The reason we have the below test is to show the missing capabilities of the OptimisticDirectExecution pipeline.
        // Currently this pipeline cannot handle distributed queries as it does not have the logic to sum up the values it gets from the backend in partial results.
        // This functionality is available for other pipelines such as the ParallelCrossPartitionQueryPipelineStage.
        [TestMethod]
        public async Task TestPipelineForDistributedQueryAsync()
        {
            int numItems = 100;
            OptimisticDirectExecutionTestInput input = CreateInput(
                    description: @"Single Partition Key and Value Field",
                    query: "SELECT AVG(c) FROM c",
                    expectedOptimisticDirectExecution: false,
                    partitionKeyPath: @"/pk",
                    partitionKeyValue: "a");

            int result = await this.GetPipelineAndDrainAsync(
                            input,
                            numItems: numItems,
                            isMultiPartition: false,
                            expectedContinuationTokenCount: 0);

            //TODO: Add validation for actual value of average
            Assert.AreEqual(1, result);
        }

        [TestMethod]
        public async Task TestHandlingOfFailedFallbackPipeline()
        {
            List<CosmosElement> documents = new List<CosmosElement>();
            int numItems = 100;
            int moveNextAsyncCounter = 0;

            List<OptimisticDirectExecutionTestInput> input = new List<OptimisticDirectExecutionTestInput>
            {
                CreateInput(
                description: @"Single Partition Key and Value Field",
                query: "SELECT * FROM c",
                expectedOptimisticDirectExecution: true,
                partitionKeyPath: @"/pk",
                partitionKeyValue: "a",
                continuationToken: null)
            };

            CosmosException goneException = await CreateGoneException();
            DocumentContainer inMemoryCollection = await CreateDocumentContainerAsync(
                    numItems,
                    multiPartition: false,
                    failureConfigs: new FlakyDocumentContainer.FailureConfigs(
                        inject429s: false,
                        injectEmptyPages: false,
                        shouldReturnFailure: () => Task.FromResult<Exception>(moveNextAsyncCounter == 1 ? goneException : null)));

            QueryRequestOptions queryRequestOptions = GetQueryRequestOptions(enableOptimisticDirectExecution: true);
            (CosmosQueryExecutionContextFactory.InputParameters inputParameters, CosmosQueryContextCore cosmosQueryContextCore) = CreateInputParamsAndQueryContext(input[0], queryRequestOptions);

            ContainerQueryProperties containerQueryProperties = new ContainerQueryProperties(
                    null,
                    null,
                    new PartitionKeyDefinition(),
                    It.IsAny<Cosmos.GeospatialType>());

            TryCatch<IQueryPipelineStage> monadicQueryPipelineStage = OptimisticDirectExecutionQueryPipelineStage.MonadicCreate(
                documentContainer: inMemoryCollection,
                inputParameters: inputParameters,
                targetRange: FeedRangeEpk.FullRange,
                queryPaginationOptions: new QueryPaginationOptions(pageSizeHint: 10),
                fallbackQueryPipelineStageFactory: (continuationToken) =>
                {
                    InputParameters updatedInputParameters = new InputParameters(
                      inputParameters.SqlQuerySpec,
                       CosmosString.Create("asdf"),
                      inputParameters.InitialFeedRange,
                      inputParameters.MaxConcurrency,
                      inputParameters.MaxItemCount,
                      inputParameters.MaxBufferedItemCount,
                      inputParameters.PartitionKey,
                      inputParameters.Properties,
                      inputParameters.PartitionedQueryExecutionInfo,
                      inputParameters.ExecutionEnvironment,
                      inputParameters.ReturnResultsInDeterministicOrder,
                      inputParameters.ForcePassthrough,
                      inputParameters.TestInjections);
                    
                    Task<TryCatch<IQueryPipelineStage>> tryCreateContext =
                        CosmosQueryExecutionContextFactory.TryCreateOdeFallbackPipelineAsync(
                            inMemoryCollection,
                            cosmosQueryContextCore,
                            containerQueryProperties,
                            updatedInputParameters,
                            NoOpTrace.Singleton,
                            default);

                    return tryCreateContext;
                },
                cancellationToken: default);

            while (await monadicQueryPipelineStage.Result.MoveNextAsync(NoOpTrace.Singleton))
            {
                moveNextAsyncCounter++;

                TryCatch<QueryPage> tryGetPage = monadicQueryPipelineStage.Result.Current;

                if (tryGetPage.Failed)
                {
                    // failure should never come till here. Should be handled before
                    Assert.Fail();
                }

                documents.AddRange(tryGetPage.Result.Documents);
            }

            Assert.IsTrue(monadicQueryPipelineStage.Result.Current.Failed);
            Assert.IsTrue(monadicQueryPipelineStage.Result.Current.InnerMostException is MalformedContinuationTokenException);
            Assert.AreNotEqual(numItems, documents.Count);
        }

        // it creates a gone exception after the first MoveNexyAsync() call. This allows for the pipeline to return some documents before failing
        // TODO: With the addition of the merge/split support, this queryPipelineStage should be able to return all documents regardless of a gone exception happening 
        private static async Task<bool> ExecuteGoneExceptionOnODEPipeline(bool isMultiPartition)
        {
            List<CosmosElement> documents = new List<CosmosElement>();
            int numItems = 100;
            int moveNextAsyncCounter = 0;

            OptimisticDirectExecutionTestInput input = CreateInput(
                    description: @"Single Partition Key and Value Field",
                    query: "SELECT * FROM c",
                    expectedOptimisticDirectExecution: true,
                    partitionKeyPath: @"/pk",
                    partitionKeyValue: "a");

            CosmosException goneException = await CreateGoneException();
            QueryRequestOptions queryRequestOptions = GetQueryRequestOptions(enableOptimisticDirectExecution: true);

            DocumentContainer inMemoryCollection = await CreateDocumentContainerAsync(
                    numItems,
                    multiPartition: isMultiPartition,
                    failureConfigs: new FlakyDocumentContainer.FailureConfigs(
                        inject429s: false,
                        injectEmptyPages: false,
                        shouldReturnFailure: () => Task.FromResult<Exception>(moveNextAsyncCounter == 1 || moveNextAsyncCounter == 4 ? goneException : null)));

            IQueryPipelineStage queryPipelineStage = await GetOdePipelineAsync(input, inMemoryCollection, queryRequestOptions);

            while (await queryPipelineStage.MoveNextAsync(NoOpTrace.Singleton))
            {
                moveNextAsyncCounter++;
                if (moveNextAsyncCounter == 1)
                {
                    Assert.AreEqual(TestInjections.PipelineType.OptimisticDirectExecution, queryRequestOptions.TestSettings.Stats.PipelineType.Value);
                }
                else
                {
                    Assert.AreNotEqual(TestInjections.PipelineType.OptimisticDirectExecution, queryRequestOptions.TestSettings.Stats.PipelineType.Value);
                }

                TryCatch<QueryPage> tryGetPage = queryPipelineStage.Current;

                if (tryGetPage.Failed)
                {
                    // failure should never come till here. Should be handled before
                    Assert.Fail();
                }

                documents.AddRange(tryGetPage.Result.Documents);
            }

            Assert.AreEqual(100, documents.Count);
            return true;
        }

        private async Task<int> GetPipelineAndDrainAsync(OptimisticDirectExecutionTestInput input, int numItems, bool isMultiPartition, int expectedContinuationTokenCount)
        {
            QueryRequestOptions queryRequestOptions = GetQueryRequestOptions(enableOptimisticDirectExecution: true);
            DocumentContainer inMemoryCollection = await CreateDocumentContainerAsync(numItems, multiPartition: isMultiPartition);
            IQueryPipelineStage queryPipelineStage = await GetOdePipelineAsync(input, inMemoryCollection, queryRequestOptions);

            List<CosmosElement> documents = new List<CosmosElement>();
            int continuationTokenCount = 0;

            while (await queryPipelineStage.MoveNextAsync(NoOpTrace.Singleton))
            {
                Assert.AreEqual(TestInjections.PipelineType.OptimisticDirectExecution, queryRequestOptions.TestSettings.Stats.PipelineType.Value);

                TryCatch<QueryPage> tryGetPage = queryPipelineStage.Current;
                tryGetPage.ThrowIfFailed();

                documents.AddRange(tryGetPage.Result.Documents);

                if (tryGetPage.Result.State == null)
                {
                    break;
                }
                else
                {
                    input = CreateInput(
                        description: input.Description,
                        query: input.Query,
                        expectedOptimisticDirectExecution: input.ExpectedOptimisticDirectExecution,
                        partitionKeyPath: @"/pk",
                        partitionKeyValue: input.PartitionKeyValue,
                        continuationToken: tryGetPage.Result.State.Value);

                    queryPipelineStage = await GetOdePipelineAsync(input, inMemoryCollection, queryRequestOptions);
                }

                continuationTokenCount++;
            }

            Assert.AreEqual(expectedContinuationTokenCount, continuationTokenCount);
            return documents.Count;
        }

        private static PartitionedQueryExecutionInfo GetPartitionedQueryExecutionInfo(string querySpecJsonString, PartitionKeyDefinition pkDefinition)
        {
            TryCatch<PartitionedQueryExecutionInfo> tryGetQueryPlan = QueryPartitionProviderTestInstance.Object.TryGetPartitionedQueryExecutionInfo(
                querySpecJsonString: querySpecJsonString,
                partitionKeyDefinition: pkDefinition,
                requireFormattableOrderByQuery: true,
                isContinuationExpected: true,
                allowNonValueAggregateQuery: true,
                hasLogicalPartitionKey: false,
                allowDCount: true,
                useSystemPrefix: false,
                geospatialType: Cosmos.GeospatialType.Geography);

            return tryGetQueryPlan.Result;
        }

        private static async Task<IQueryPipelineStage> GetOdePipelineAsync(OptimisticDirectExecutionTestInput input, DocumentContainer documentContainer, QueryRequestOptions queryRequestOptions)
        {
            (CosmosQueryExecutionContextFactory.InputParameters inputParameters, CosmosQueryContextCore cosmosQueryContextCore) = CreateInputParamsAndQueryContext(input, queryRequestOptions);

            IQueryPipelineStage queryPipelineStage = CosmosQueryExecutionContextFactory.Create(
                      documentContainer,
                      cosmosQueryContextCore,
                      inputParameters,
                      NoOpTrace.Singleton);

            Assert.IsNotNull(queryPipelineStage);
            return queryPipelineStage;
        }

        private static async Task<CosmosException> CreateGoneException()
        {
            string goneExceptionMessage = $"Epk Range: Partition does not exist at the given range.";
            return new CosmosException(
                message: goneExceptionMessage,
                statusCode: System.Net.HttpStatusCode.Gone,
                subStatusCode: (int)SubStatusCodes.PartitionKeyRangeGone,
                activityId: "0f8fad5b-d9cb-469f-a165-70867728950e",
                requestCharge: default);
        }

        private static async Task<DocumentContainer> CreateDocumentContainerAsync(
            int numItems,
            bool multiPartition,
            FlakyDocumentContainer.FailureConfigs failureConfigs = null)
        {
            PartitionKeyDefinition partitionKeyDefinition = new PartitionKeyDefinition()
            {
                Paths = new System.Collections.ObjectModel.Collection<string>()
                {
                    "/pk"
                },
                Kind = PartitionKind.Hash,
                Version = PartitionKeyDefinitionVersion.V2,
            };

            IMonadicDocumentContainer monadicDocumentContainer = new InMemoryContainer(partitionKeyDefinition);
            if (failureConfigs != null)
            {
                monadicDocumentContainer = new FlakyDocumentContainer(monadicDocumentContainer, failureConfigs, isODETest: true);
            }

            DocumentContainer documentContainer = new DocumentContainer(monadicDocumentContainer);

            // a value of 2 would lead to 4 partitions (2 * 2). 4 partitions are used because they're easy to manage + demonstrates multi partition use case
            int exponentPartitionKeyRanges = 2;

            IReadOnlyList<FeedRangeInternal> ranges;

            for (int i = 0; i < exponentPartitionKeyRanges; i++)
            {
                ranges = await documentContainer.GetFeedRangesAsync(
                    trace: NoOpTrace.Singleton,
                    cancellationToken: default);

                if (multiPartition)
                {
                    foreach (FeedRangeInternal range in ranges)
                    {
                        await documentContainer.SplitAsync(range, cancellationToken: default);
                    }
                }

                await documentContainer.RefreshProviderAsync(NoOpTrace.Singleton, cancellationToken: default);
            }

            ranges = await documentContainer.GetFeedRangesAsync(
                    trace: NoOpTrace.Singleton,
                    cancellationToken: default);

            int rangeCount = multiPartition ? 4 : 1;

            Assert.AreEqual(rangeCount, ranges.Count);

            for (int i = 0; i < numItems; i++)
            {
                // Insert an item
                CosmosObject item = CosmosObject.Parse($"{{\"pk\" : \"a\" }}");
                TryCatch<Record> monadicCreateRecord = await documentContainer.MonadicCreateItemAsync(item, cancellationToken: default);
                Assert.IsTrue(monadicCreateRecord.Succeeded);
            }

            return documentContainer;
        }

        private static OptimisticDirectExecutionTestInput CreateInput(
            string description,
            string query,
            bool expectedOptimisticDirectExecution,
            string partitionKeyPath,
            string partitionKeyValue,
            CosmosElement continuationToken = null,
            bool expectException = false)
        {
            PartitionKeyBuilder pkBuilder = new PartitionKeyBuilder();
            pkBuilder.Add(partitionKeyValue);

            return CreateInput(description, query, expectedOptimisticDirectExecution, partitionKeyPath, pkBuilder.Build(), continuationToken, expectException);
        }

        private static OptimisticDirectExecutionTestInput CreateInput(
            string description,
            string query,
            bool expectedOptimisticDirectExecution,
            string partitionKeyPath,
            Cosmos.PartitionKey partitionKeyValue,
            CosmosElement continuationToken = null,
            bool expectException = false)
        {
            return new OptimisticDirectExecutionTestInput(description, query, new SqlQuerySpec(query), expectedOptimisticDirectExecution, partitionKeyPath, partitionKeyValue, continuationToken, expectException);
        }

        public override OptimisticDirectExecutionTestOutput ExecuteTest(OptimisticDirectExecutionTestInput input)
        {
            // gets DocumentContainer
            IMonadicDocumentContainer monadicDocumentContainer = new InMemoryContainer(input.PartitionKeyDefinition);
            DocumentContainer documentContainer = new DocumentContainer(monadicDocumentContainer);

            QueryRequestOptions queryRequestOptions = GetQueryRequestOptions(enableOptimisticDirectExecution: true);

            (CosmosQueryExecutionContextFactory.InputParameters inputParameters, CosmosQueryContextCore cosmosQueryContextCore) = CreateInputParamsAndQueryContext(input, queryRequestOptions);

            IQueryPipelineStage queryPipelineStage = CosmosQueryExecutionContextFactory.Create(
                      documentContainer,
                      cosmosQueryContextCore,
                      inputParameters,
                      NoOpTrace.Singleton);

            bool result = queryPipelineStage.MoveNextAsync(NoOpTrace.Singleton).Result;

            if (input.ExpectException)
            {
                return new OptimisticDirectExecutionTestOutput(!queryPipelineStage.Current.Failed);
            }

            if (input.ExpectedOptimisticDirectExecution)
            {
                Assert.AreEqual(TestInjections.PipelineType.OptimisticDirectExecution, queryRequestOptions.TestSettings.Stats.PipelineType.Value);
            }
            else
            {
                Assert.AreNotEqual(TestInjections.PipelineType.OptimisticDirectExecution, queryRequestOptions.TestSettings.Stats.PipelineType.Value);
            }

            Assert.IsNotNull(queryPipelineStage);
            Assert.IsTrue(result);

            return new OptimisticDirectExecutionTestOutput(input.ExpectedOptimisticDirectExecution);
        }

        private static Tuple<CosmosQueryExecutionContextFactory.InputParameters, CosmosQueryContextCore> CreateInputParamsAndQueryContext(OptimisticDirectExecutionTestInput input, QueryRequestOptions queryRequestOptions)
        {
            CosmosSerializerCore serializerCore = new();
            using StreamReader streamReader = new(serializerCore.ToStreamSqlQuerySpec(new SqlQuerySpec(input.Query), Documents.ResourceType.Document));
            string sqlQuerySpecJsonString = streamReader.ReadToEnd();

            PartitionedQueryExecutionInfo partitionedQueryExecutionInfo = GetPartitionedQueryExecutionInfo(sqlQuerySpecJsonString, input.PartitionKeyDefinition);
            if (input.PartitionKeyValue == default || input.PartitionKeyValue == Cosmos.PartitionKey.None)
            {
                input.PartitionKeyValue = Cosmos.PartitionKey.Null;
            }

            CosmosQueryExecutionContextFactory.InputParameters inputParameters = new CosmosQueryExecutionContextFactory.InputParameters(
                sqlQuerySpec: new SqlQuerySpec(input.Query),
                initialUserContinuationToken: input.ContinuationToken,
                initialFeedRange: null,
                maxConcurrency: queryRequestOptions.MaxConcurrency,
                maxItemCount: queryRequestOptions.MaxItemCount,
                maxBufferedItemCount: queryRequestOptions.MaxBufferedItemCount,
                partitionKey: input.PartitionKeyValue,
                properties: queryRequestOptions.Properties,
                partitionedQueryExecutionInfo: partitionedQueryExecutionInfo,
                executionEnvironment: null,
                returnResultsInDeterministicOrder: null,
                forcePassthrough: true,
                testInjections: queryRequestOptions.TestSettings);

            string databaseId = "db1234";
            string resourceLink = $"dbs/{databaseId}/colls";
            CosmosQueryContextCore cosmosQueryContextCore = new CosmosQueryContextCore(
                client: new TestCosmosQueryClient(),
                resourceTypeEnum: Documents.ResourceType.Document,
                operationType: Documents.OperationType.Query,
                resourceType: typeof(QueryResponseCore),
                resourceLink: resourceLink,
                isContinuationExpected: true,
                allowNonValueAggregateQuery: true,
                useSystemPrefix: false,
                correlatedActivityId: Guid.NewGuid());

            return Tuple.Create(inputParameters, cosmosQueryContextCore);
        }

        private static QueryRequestOptions GetQueryRequestOptions(bool enableOptimisticDirectExecution)
        {
            return new QueryRequestOptions
            {
                MaxConcurrency = 0,
                MaxItemCount = 10,
                MaxBufferedItemCount = 7000,
                TestSettings = new TestInjections(simulate429s: true, simulateEmptyPages: false, enableOptimisticDirectExecution: enableOptimisticDirectExecution, new TestInjections.ResponseStats()),
                Properties = new Dictionary<string, object>()
            {
                { HttpConstants.HttpHeaders.EnumerationDirection, "test" },
            }
            };
        }
    }

    public sealed class OptimisticDirectExecutionTestOutput : BaselineTestOutput
    {
        public OptimisticDirectExecutionTestOutput(bool executeAsOptimisticDirectExecution)
        {
            this.ExecuteAsOptimisticDirectExecution = executeAsOptimisticDirectExecution;
        }

        public bool ExecuteAsOptimisticDirectExecution { get; }

        public override void SerializeAsXml(XmlWriter xmlWriter)
        {
            xmlWriter.WriteStartElement(nameof(this.ExecuteAsOptimisticDirectExecution));
            xmlWriter.WriteValue(this.ExecuteAsOptimisticDirectExecution);
            xmlWriter.WriteEndElement();
        }
    }

    public sealed class OptimisticDirectExecutionTestInput : BaselineTestInput
    {
        internal PartitionKeyDefinition PartitionKeyDefinition { get; set; }
        internal SqlQuerySpec SqlQuerySpec { get; set; }
        internal Cosmos.PartitionKey PartitionKeyValue { get; set; }
        internal bool ExpectedOptimisticDirectExecution { get; set; }
        internal PartitionKeyRangeIdentity PartitionKeyRangeId { get; set; }
        internal string Query { get; set; }
        internal CosmosElement ContinuationToken { get; set; }
        internal bool ExpectException { get; set; }

        internal OptimisticDirectExecutionTestInput(
            string description,
            string query,
            SqlQuerySpec sqlQuerySpec,
            bool expectedOptimisticDirectExecution,
            string partitionKeyPath,
            Cosmos.PartitionKey partitionKeyValue,
            CosmosElement continuationToken,
            bool expectException)
            : base(description)
        {
            this.PartitionKeyDefinition = new PartitionKeyDefinition()
            {
                Paths = new Collection<string>()
                {
                    partitionKeyPath
                },
                Kind = PartitionKind.Hash,
                Version = PartitionKeyDefinitionVersion.V2,
            };
            this.SqlQuerySpec = sqlQuerySpec;
            this.ExpectedOptimisticDirectExecution = expectedOptimisticDirectExecution;
            this.Query = query;
            this.PartitionKeyValue = partitionKeyValue;
            this.ContinuationToken = continuationToken;
            this.ExpectException = expectException;
        }

        public override void SerializeAsXml(XmlWriter xmlWriter)
        {
            xmlWriter.WriteElementString("Description", this.Description);
            xmlWriter.WriteElementString("Query", this.SqlQuerySpec.QueryText);
            xmlWriter.WriteStartElement("PartitionKeys");
            if (this.PartitionKeyDefinition != null)
            {
                foreach (string path in this.PartitionKeyDefinition.Paths)
                {
                    xmlWriter.WriteElementString("Key", path);
                }
            }

            xmlWriter.WriteEndElement();
            if (this.PartitionKeyDefinition != null)
            {
                xmlWriter.WriteElementString(
                    "PartitionKeyType",
                    this.PartitionKeyDefinition.Kind == PartitionKind.Hash ? "Hash" : (
                        this.PartitionKeyDefinition.Kind == PartitionKind.MultiHash ? "MultiHash" : "Range"));
            }

            if (this.SqlQuerySpec.ShouldSerializeParameters())
            {
                xmlWriter.WriteStartElement("QueryParameters");
                xmlWriter.WriteCData(JsonConvert.SerializeObject(
                    this.SqlQuerySpec.Parameters,
                    Newtonsoft.Json.Formatting.Indented));
                xmlWriter.WriteEndElement();
            }
        }
    }

    internal class TestCosmosQueryClient : CosmosQueryClient
    {
        public override Action<IQueryable> OnExecuteScalarQueryCallback => throw new NotImplementedException();

        public override bool ByPassQueryParsing()
        {
            return false;
        }

        public override void ClearSessionTokenCache(string collectionFullName)
        {
            throw new NotImplementedException();
        }

        public override Task<TryCatch<QueryPage>> ExecuteItemQueryAsync(string resourceUri, ResourceType resourceType, OperationType operationType, Guid clientQueryCorrelationId, Cosmos.FeedRange feedRange, QueryRequestOptions requestOptions, SqlQuerySpec sqlQuerySpec, string continuationToken, bool isContinuationExpected, int pageSize, ITrace trace, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public override Task<PartitionedQueryExecutionInfo> ExecuteQueryPlanRequestAsync(string resourceUri, ResourceType resourceType, OperationType operationType, SqlQuerySpec sqlQuerySpec, Cosmos.PartitionKey? partitionKey, string supportedQueryFeatures, Guid clientQueryCorrelationId, ITrace trace, CancellationToken cancellationToken)
        {
            return Task.FromResult(new PartitionedQueryExecutionInfo());
        }

        public override Task ForceRefreshCollectionCacheAsync(string collectionLink, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public override Task<ContainerQueryProperties> GetCachedContainerQueryPropertiesAsync(string containerLink, Cosmos.PartitionKey? partitionKey, ITrace trace, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ContainerQueryProperties(
                "test",
                WFConstants.BackendHeaders.EffectivePartitionKeyString,
                new PartitionKeyDefinition(),
                Cosmos.GeospatialType.Geometry));
        }

        public override Task<List<PartitionKeyRange>> GetTargetPartitionKeyRangeByFeedRangeAsync(string resourceLink, string collectionResourceId, PartitionKeyDefinition partitionKeyDefinition, FeedRangeInternal feedRangeInternal, bool forceRefresh, ITrace trace)
        {
            throw new NotImplementedException();
        }

        public override Task<List<PartitionKeyRange>> GetTargetPartitionKeyRangesAsync(string resourceLink, string collectionResourceId, List<Range<string>> providedRanges, bool forceRefresh, ITrace trace)
        {
            return Task.FromResult(new List<PartitionKeyRange>{new PartitionKeyRange()
            {
                MinInclusive = PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey,
                MaxExclusive = PartitionKeyInternal.MaximumExclusiveEffectivePartitionKey
            }
            });
        }

        public override Task<List<PartitionKeyRange>> GetTargetPartitionKeyRangesByEpkStringAsync(string resourceLink, string collectionResourceId, string effectivePartitionKeyString, bool forceRefresh, ITrace trace)
        {
            return Task.FromResult(new List<PartitionKeyRange>{new PartitionKeyRange()
            {
                MinInclusive = PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey,
                MaxExclusive = PartitionKeyInternal.MaximumExclusiveEffectivePartitionKey
            }
            });
        }

        public override Task<IReadOnlyList<PartitionKeyRange>> TryGetOverlappingRangesAsync(string collectionResourceId, Range<string> range, bool forceRefresh = false)
        {
            throw new NotImplementedException();
        }

        public override async Task<TryCatch<PartitionedQueryExecutionInfo>> TryGetPartitionedQueryExecutionInfoAsync(SqlQuerySpec sqlQuerySpec, ResourceType resourceType, PartitionKeyDefinition partitionKeyDefinition, bool requireFormattableOrderByQuery, bool isContinuationExpected, bool allowNonValueAggregateQuery, bool hasLogicalPartitionKey, bool allowDCount, bool useSystemPrefix, Cosmos.GeospatialType geospatialType, CancellationToken cancellationToken)
        {
            PartitionedQueryExecutionInfo partitionedQueryExecutionInfo = new PartitionedQueryExecutionInfo()
            {
                QueryInfo = new QueryInfo()
                {
                    Aggregates = null,
                    DistinctType = DistinctQueryType.None,
                    GroupByAliases = null,
                    GroupByAliasToAggregateType = null,
                    GroupByExpressions = null,
                    HasSelectValue = false,
                    Limit = null,
                    Offset = null,
                    OrderBy = null,
                    OrderByExpressions = null,
                    RewrittenQuery = null,
                    Top = null,
                },
                QueryRanges = new List<Documents.Routing.Range<string>>(),
            };

            return TryCatch<PartitionedQueryExecutionInfo>.FromResult(partitionedQueryExecutionInfo);
        }
    }
}
