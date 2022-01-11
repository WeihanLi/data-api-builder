using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.DataGateway.Service.configurations;
using Azure.DataGateway.Service.Controllers;
using Azure.DataGateway.Service.Resolvers;
using Azure.DataGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace Azure.DataGateway.Service.Tests.SqlTests
{
    /// <summary>
    /// Base class providing common test fixture for both REST and GraphQL tests.
    /// </summary>
    [TestClass]
    public abstract class SqlTestBase
    {
        private static string _testCategory;
        protected static IQueryExecutor _queryExecutor;
        protected static IQueryBuilder _queryBuilder;
        protected static IQueryEngine _queryEngine;
        protected static IMutationEngine _mutationEngine;
        protected static IMetadataStoreProvider _metadataStoreProvider;
        protected static Mock<IAuthorizationService> _authorizationService;
        protected static Mock<IHttpContextAccessor> _httpContextAccessor;

        /// <summary>
        /// Sets up test fixture for class, only to be run once per test run.
        /// This is a helper that is called from the non abstract versions of
        /// this class.
        /// </summary>
        /// <param name="context"></param>
        protected static async Task InitializeTestFixture(TestContext context, string tableName, string testCategory)
        {
            _testCategory = testCategory;

            IOptions<DataGatewayConfig> config = SqlTestHelper.LoadConfig($"{_testCategory}IntegrationTest");

            switch (_testCategory)
            {
                case TestCategory.POSTGRESQL:
                    _queryExecutor = new QueryExecutor<NpgsqlConnection>(config);
                    _queryBuilder = new PostgresQueryBuilder();
                    break;
                case TestCategory.MSSQL:
                    _queryExecutor = new QueryExecutor<SqlConnection>(config);
                    _queryBuilder = new MsSqlQueryBuilder();
                    break;
            }

            // Setup AuthorizationService to always return Authorized.
            _authorizationService = new Mock<IAuthorizationService>();
            _authorizationService.Setup(x => x.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<object>(),
                It.IsAny<IEnumerable<IAuthorizationRequirement>>()
                ).Result).Returns(AuthorizationResult.Success);

            // Setup Mock HttpContextAccess to return user as required when calling AuthorizationService.AuthorizeAsync
            _httpContextAccessor = new Mock<IHttpContextAccessor>();
            _httpContextAccessor.Setup(x => x.HttpContext.User).Returns(new ClaimsPrincipal());

            _metadataStoreProvider = new FileMetadataStoreProvider("sql-config.json");
            _queryEngine = new SqlQueryEngine(_metadataStoreProvider, _queryExecutor, _queryBuilder);
            _mutationEngine = new SqlMutationEngine(_queryEngine, _metadataStoreProvider, _queryExecutor, _queryBuilder);

            await ResetDbStateAsync();
        }

        protected static async Task ResetDbStateAsync()
        {
            using DbDataReader _ = await _queryExecutor.ExecuteQueryAsync(File.ReadAllText($"{_testCategory}Books.sql"), parameters: null);
        }

        /// <summary>
        /// returns httpcontext with body consisting of the given data.
        /// </summary>
        /// <param name="data">The data to be put in the request body e.g. GraphQLQuery</param>
        /// <returns>The http context with given data as stream of utf-8 bytes.</returns>
        protected static DefaultHttpContext GetHttpContextWithBody(string data)
        {
            MemoryStream stream = new(Encoding.UTF8.GetBytes(data));
            DefaultHttpContext httpContext = new()
            {
                Request = { Body = stream, ContentLength = stream.Length }
            };
            return httpContext;
        }

        /// <summary>
        /// Constructs an http context with request consisting of the given query string.
        /// </summary>
        /// <param name="queryStringUrl">query</param>
        /// <returns>The http context with request consisting of the given query string.</returns>
        protected static DefaultHttpContext GetHttpContextWithQueryString(string queryStringUrl)
        {
            DefaultHttpContext httpContext = new()
            {
                Request = { QueryString = new(queryStringUrl) }
            };

            return httpContext;
        }

        /// <summary>
        /// Sends raw SQL query to database engine to retrieve expected result in JSON format.
        /// </summary>
        /// <param name="queryText">raw database query</param>
        /// <returns>string in JSON format</returns>
        protected static async Task<string> GetDatabaseResultAsync(string queryText)
        {
            using DbDataReader reader = await _queryExecutor.ExecuteQueryAsync(queryText, parameters: null);

            // An empty result will cause an error with the json parser
            if (!reader.HasRows)
            {
                throw new System.Exception("No rows to read from database result");
            }

            using JsonDocument sqlResult = JsonDocument.Parse(await SqlQueryEngine.GetJsonStringFromDbReader(reader));

            JsonElement sqlResultData = sqlResult.RootElement;

            return sqlResultData.ToString();
        }

        /// <summary>
        /// Does the setup required to perform a test of the REST Api for both
        /// MsSql and Postgress. Shared setup logic eliminates some code duplication
        /// between MsSql and Postgress.
        /// </summary>
        /// <param name="primaryKeyRoute">string represents the primary key route</param>
        /// <param name="queryString">string represents the query string provided in URL</param>
        /// <param name="entity">string represents the name of the entity</param>
        /// <param name="sqlQuery">string represents the query to be executed</param>
        /// <param name="controller">string represents the rest controller</param>
        /// <param name="exception">bool represents if we expect an exception</param>
        /// <param name="expectedErrorMessage">string represents the error message in the JsonResponse</param>
        /// <param name="expectedStatusCode">int represents the returned http status code</param>
        /// <param name="expectedSubStatusCode">enum represents the returned sub status code</param>
        /// <returns></returns>
        protected static async Task SetupAndRunRestApiTest(
            string primaryKeyRoute,
            string queryString,
            string entity,
            string sqlQuery,
            RestController controller,
            bool exception = false,
            string expectedErrorMessage = "",
            int expectedStatusCode = 200,
            string expectedSubStatusCode = "BadRequest")
        {
            ConfigureRestController(controller, queryString);
            // if an exception is expected we generate the correct error
            string expected = exception ? RestController.ErrorResponse(expectedSubStatusCode.ToString(), expectedErrorMessage, expectedStatusCode).Value.ToString() :
                await GetDatabaseResultAsync(sqlQuery);

            await SqlTestHelper.PerformApiTest(
                controller.Find,
                entity,
                primaryKeyRoute,
                expected,
                expectedStatusCode
            );
        }

        ///<summary>
        /// Add HttpContext with query to the RestController
        ///</summary>
        protected static void ConfigureRestController(RestController restController, string queryString)
        {
            restController.ControllerContext.HttpContext = GetHttpContextWithQueryString(queryString);
        }

        /// <summary>
        /// Read the data property of the GraphQLController result
        /// </summary>
        /// <param name="graphQLQuery"></param>
        /// <param name="graphQLQueryName"></param>
        /// <param name="graphQLController"></param>
        /// <returns>string in JSON format</returns>
        protected static async Task<string> GetGraphQLResultAsync(string graphQLQuery, string graphQLQueryName, GraphQLController graphQLController)
        {
            JsonElement graphQLResult = await GetGraphQLControllerResultAsync(graphQLQuery, graphQLQueryName, graphQLController);
            Console.WriteLine(graphQLResult.ToString());
            JsonElement graphQLResultData = graphQLResult.GetProperty("data").GetProperty(graphQLQueryName);

            // JsonElement.ToString() prints null values as empty strings instead of "null"
            return graphQLResultData.GetRawText();
        }

        /// <summary>
        /// Sends graphQL query through graphQL service, consisting of gql engine processing (resolvers, object serialization)
        /// returning the result as a JsonDocument
        /// </summary>
        /// <param name="graphQLQuery"></param>
        /// <param name="graphQLQueryName"></param>
        /// <param name="graphQLController"></param>
        /// <returns>JsonDocument</returns>
        protected static async Task<JsonElement> GetGraphQLControllerResultAsync(string graphQLQuery, string graphQLQueryName, GraphQLController graphQLController)
        {
            string graphqlQueryJson = JObject.FromObject(new
            {
                query = graphQLQuery
            }).ToString();

            Console.WriteLine(graphqlQueryJson);

            graphQLController.ControllerContext.HttpContext = GetHttpContextWithBody(graphqlQueryJson);
            return await graphQLController.PostAsync();
        }
    }
}