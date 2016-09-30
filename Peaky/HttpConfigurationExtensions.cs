// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Web.Http;
using Its.Log.Instrumentation;
using Its.Recipes;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http.Controllers;
using Microsoft.Its.Recipes;

namespace Peaky
{
    /// <summary>
    /// Provides methods for configuring Web API to expose sensors.
    /// </summary>
    public static class HttpConfigurationExtensions
    {
        internal const string TestRootRouteName = "Peaky-Tests";

        /// <summary>
        /// Configures API routes to expose sensors.
        /// </summary>
        /// <param name="configuration">The HTTP configuration.</param>
        /// <param name="authorizeRequest">A delegate providing a method to authorize or deny sensor requests.</param>
        /// <param name="baseUri">The base URI at which sensors will be routed.</param>
        /// <param name="handler">An optional message handler to be invoked when sensors are called.</param>
        /// <returns>
        /// The updated <see cref="HttpConfiguration" />.
        /// </returns>
        /// <exception cref="System.ArgumentNullException">configuration
        /// or
        /// authorizeRequest</exception>
        public static HttpConfiguration MapSensorRoutes(
            this HttpConfiguration configuration,
            Func<HttpActionContext, bool> authorizeRequest,
            string baseUri = "sensors",
            HttpMessageHandler handler = null)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }
            if (authorizeRequest == null)
            {
                throw new ArgumentNullException(nameof(authorizeRequest));
            }

            AuthorizeSensorsAttribute.AuthorizeRequest = authorizeRequest;

            configuration.Routes.MapHttpRoute(
                "Peaky-Sensors",
                baseUri.AppendSegment("{name}"),
                defaults: new
                {
                    controller = "Sensor",
                    name = RouteParameter.Optional
                },
                constraints: null,
                handler: handler);

            return configuration;
        }

        /// <summary>
        /// Discovers tests in the application and maps routes to allow them to be called over HTTP.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        /// <param name="configureTargets">A delegate to configure the test targets.</param>
        /// <param name="baseUri">The base URI segment that the tests will be routed under.</param>
        /// <param name="concreateTestClasses">The Types to map routes for. If omitted, all discovered implementations of <see cref="IPeakyTest" /> will be routed.</param>
        /// <param name="handler">A message handler to handle test requests, if you would like to provide authentication, logging, or other functionality during calls to peaky tests.</param>
        /// <param name="testUiScriptUrl">The location of the test UI script.</param>
        /// <param name="testUiLibraryUrls">The locations of any libraries the UI script depends on</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">configuration</exception>
        public static HttpConfiguration MapTestRoutes(
            this HttpConfiguration configuration,
            Action<TestTargetRegistry> configureTargets = null,
            string baseUri = "tests",
            IEnumerable<Type> concreateTestClasses = null,
            HttpMessageHandler handler = null,
            string testUiScriptUrl = "//phillippruett.github.io/Peaky/javascripts/peaky.js",
            IEnumerable<string> testUiLibraryUrls = null,
            IEnumerable<string> styleSheetUrls = null)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (!Trace.Listeners.OfType<TracingFilter.TraceListener>().Any())
            {
                var traceListener = new TracingFilter.TraceListener();
                Trace.Listeners.Add(traceListener);
                Log.EntryPosted += (_, args) => traceListener.WriteLine(args.LogEntry.ToLogString());
            }

            // set up specialized handling on the specified routes
            configuration.Filters.Add(new TestErrorFilter(baseUri));
            if (!string.IsNullOrEmpty(testUiScriptUrl) &&
                Uri.IsWellFormedUriString(testUiScriptUrl, UriKind.RelativeOrAbsolute))
            {
                configuration.TestUiUriIs(testUiScriptUrl);
            }

            var styleSheetUrlsArray = styleSheetUrls
                .IfNotNull()
                .Then(a => a.Where(u => !string.IsNullOrEmpty(u)
                                        && Uri.IsWellFormedUriString(u, UriKind.RelativeOrAbsolute))
                    .ToArray())
                .Else(() => new string[] { "//phillippruett.github.io/Peaky/stylesheets/peaky.css" });
            configuration.StyleSheets(styleSheetUrlsArray);

            var testUiLibraryUrlsArray = testUiLibraryUrls
                .IfNotNull()
                .Then(a => a.Where(u => !string.IsNullOrEmpty(u)
                                        && Uri.IsWellFormedUriString(u, UriKind.RelativeOrAbsolute))
                    .ToArray())
                .Else(() => new string[] {  });
            configuration.TestLibraryUrisAre(testUiLibraryUrlsArray);

            var testRootRouteTemplate = baseUri.AppendSegment("{environment}/{application}");
            configuration.RootTestUriIs(baseUri);

            // set up test discovery routes
            configuration.Routes.MapHttpRoute(
                TestRootRouteName,
                testRootRouteTemplate,
                defaults: new
                {
                    controller = "PeakyTest",
                    action = "tests",
                    application = RouteParameter.Optional,
                    environment = RouteParameter.Optional
                },
                constraints: null,
                handler: handler);

            // set up test execution routes
            var targetRegistry = new TestTargetRegistry();
            configuration.TestTargetsAre(targetRegistry);
            configureTargets?.Invoke(targetRegistry);

            concreateTestClasses = concreateTestClasses ?? Discover.ConcreteTypes()
                .DerivedFrom(typeof (IPeakyTest));

            var testDefinitions = concreateTestClasses.GetTestDefinitions();
            configuration.TestDefinitionsAre(testDefinitions);

            testDefinitions.Select(p => p.Value)
                           .ForEach(test =>
                           {
                               var targetConstraint = new TargetConstraint(test);
                               
                               targetRegistry.ForEach(target => { Task.Run(() => targetConstraint.Match(target)); });

                               configuration.Routes.MapHttpRoute(
                                   test.RouteName,
                                   testRootRouteTemplate.AppendSegment(test.TestName),
                                   defaults: new
                                   {
                                       controller = "PeakyTest",
                                       action = "run",
                                       testName = test.TestName
                                   },
                                   constraints: new
                                   {
                                       tag = new TagConstraint(test),
                                       application = new ApplicationConstraint(test),
                                       environment = new EnvironmentConstraint(test),
                                       target = targetConstraint
                                   },
                                   handler: handler
                                   );
                           });

            return configuration;
        }

        internal static void RootTestUriIs(
            this HttpConfiguration configuration,
            string uriRoot) =>
                configuration.Properties["Peaky.RootTestUri"] = uriRoot;

        internal static string RootTestUri(
            this HttpConfiguration configuration) =>
                (string) configuration.Properties["Peaky.RootTestUri"];

        internal static void TestUiUriIs(
            this HttpConfiguration configuration,
            string testUiUri) =>
                configuration.Properties["Peaky.TestUiUri"] = testUiUri;

        internal static void StyleSheets(
            this HttpConfiguration configuration,
            IEnumerable<string> styleSheet) =>
                configuration.Properties["Peaky.StyleSheets"] = styleSheet;

        internal static void TestLibraryUrisAre(
            this HttpConfiguration configuration,
            IEnumerable<string> testUiUri) =>
                configuration.Properties["Peaky.TestLibraryUris"] = testUiUri;

        internal static void TestDefinitionsAre(
            this HttpConfiguration configuration,
            IDictionary<string, TestDefinition> testDefinitions) =>
                configuration.Properties["Peaky.TestDefinitions"] = testDefinitions;

        internal static TestDefinition TestDefinition(
            this HttpConfiguration configuration,
            string testName) =>
                configuration.TestDefinitions()[testName];

        internal static IDictionary<string, TestDefinition> TestDefinitions(
            this HttpConfiguration configuration) =>
                ((IDictionary<string, TestDefinition>) configuration.Properties["Peaky.TestDefinitions"]);

        internal static void TestTargetsAre(
            this HttpConfiguration configuration,
            TestTargetRegistry targets) =>
                configuration.Properties["Peaky.TestTargets"] = targets;

        internal static TestTargetRegistry TestTargets(this HttpConfiguration configuration) =>
            (TestTargetRegistry) configuration.Properties["Peaky.TestTargets"];
    }
}
