﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.AspNetCore.Mvc.Razor.Extensions;
using Microsoft.AspNetCore.Razor.Evolution;
using Microsoft.AspNetCore.Razor.Evolution.IntegrationTests;
using Microsoft.AspNetCore.Razor.Evolution.Intermediate;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Razor;
using Xunit;
using Xunit.Sdk;

namespace Microsoft.AspNetCore.Razor.Test.Common
{
    [IntializeTestFile]
    public abstract class IntegrationTestBase
    {
        private Type _parentClassType;

        public IntegrationTestBase(Type parentClassType)
        {
            _parentClassType = parentClassType;
        }

        protected Assembly Assembly { get { return _parentClassType.GetTypeInfo().Assembly; } }

#if GENERATE_BASELINES
        private static readonly bool GenerateBaselines = true;
#else
        private static readonly bool GenerateBaselines = false;
#endif

        private static readonly AsyncLocal<string> _filename = new AsyncLocal<string>();

        protected string TestProjectRoot { get { return TestProject.GetProjectDirectory(_parentClassType); } }

        // Used by the test framework to set the 'base' name for test files.
        public static string Filename
        {
            get { return _filename.Value; }
            set { _filename.Value = value; }
        }

        protected RazorEngine CreateRazorEngine(
            IEnumerable<TagHelperDescriptor> descriptors,
            bool designTime,
            IEnumerable<IRazorEngineFeature> features)
        {
            return RazorEngine.Create(b =>
            {
                RazorExtensions.Register(b);

                if (designTime)
                {
                    b.Features.Add(new DesignTimeParserOptionsFeature());
                }

                foreach(var feature in features)
                {
                    b.Features.Add(feature);
                }

                if (descriptors != null)
                {
                    b.AddTagHelpers(descriptors);
                }
                else
                {
                    b.Features.Add(new DefaultTagHelperFeature());
                }
            });
        }

        protected virtual RazorCodeDocument CreateCodeDocument()
        {
            if (Filename == null)
            {
                var message = $"{nameof(CreateCodeDocument)} should only be called from an integration test ({nameof(Filename)} is null).";
                throw new InvalidOperationException(message);
            }

            var sourceFilename = Path.ChangeExtension(Filename, ".cshtml");
            sourceFilename = sourceFilename.Replace("_DesignTime", "");
            sourceFilename = sourceFilename.Replace("_Runtime", "");
            var testFile = new TestFile(sourceFilename, Assembly);
            if (!testFile.Exists())
            {
                throw new XunitException($"The resource {sourceFilename} was not found.");
            }

            var imports = new List<RazorSourceDocument>();
            while (true)
            {
                var importsFilename = Path.ChangeExtension(Filename + "_Imports" + imports.Count.ToString(), ".cshtml");
                var importsTestFile = new TestFile(importsFilename, Assembly);
                if (!importsTestFile.Exists())
                {
                    break;
                }

                imports.Add(TestRazorSourceDocument.CreateResource(
                    importsFilename,
                    Assembly,
                    encoding: null,
                    normalizeNewLines: true));
            }

            var codeDocument = RazorCodeDocument.Create(
                TestRazorSourceDocument.CreateResource(
                    sourceFilename,
                    Assembly,
                    encoding: null,
                    normalizeNewLines: true),
                imports);

            // This will ensure that we're not putting any randomly generated data in a baseline.
            codeDocument.Items[DefaultRazorCSharpLoweringPhase.SuppressUniqueIds] = "test";
            return codeDocument;
        }

        protected void AssertIRMatchesBaseline(DocumentIRNode document)
        {
            if (Filename == null)
            {
                var message = $"{nameof(AssertIRMatchesBaseline)} should only be called from an integration test ({nameof(Filename)} is null).";
                throw new InvalidOperationException(message);
            }

            var baselineFilename = Path.ChangeExtension(Filename, ".ir.txt");

            if (GenerateBaselines)
            {
                var baselineFullPath = Path.Combine(TestProjectRoot, baselineFilename);
                File.WriteAllText(baselineFullPath, RazorIRNodeSerializer.Serialize(document));
                return;
            }

            var testFile = new TestFile(baselineFilename, Assembly);
            if (!testFile.Exists())
            {
                throw new XunitException($"The resource {baselineFilename} was not found.");
            }

            var baseline = testFile.ReadAllText().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            RazorIRNodeVerifier.Verify(document, baseline);
        }

        protected void AssertCSharpDocumentMatchesBaseline(RazorCSharpDocument document)
        {
            if (Filename == null)
            {
                var message = $"{nameof(AssertCSharpDocumentMatchesBaseline)} should only be called from an integration test ({nameof(Filename)} is null).";
                throw new InvalidOperationException(message);
            }

            var baselineFilename = Path.ChangeExtension(Filename, ".codegen.cs");

            if (GenerateBaselines)
            {
                var baselineFullPath = Path.Combine(TestProjectRoot, baselineFilename);
                File.WriteAllText(baselineFullPath, document.GeneratedCode);
                return;
            }

            var testFile = new TestFile(baselineFilename, Assembly);
            if (!testFile.Exists())
            {
                throw new XunitException($"The resource {baselineFilename} was not found.");
            }

            var baseline = testFile.ReadAllText();

            // Normalize newlines to match those in the baseline.
            var actual = document.GeneratedCode.Replace("\r", "").Replace("\n", "\r\n");

            Assert.Equal(baseline, actual);
        }

        protected void AssertDesignTimeDocumentMatchBaseline(RazorCodeDocument document)
        {
            if (Filename == null)
            {
                var message = $"{nameof(AssertDesignTimeDocumentMatchBaseline)} should only be called from an integration test ({nameof(Filename)} is null).";
                throw new InvalidOperationException(message);
            }

            var csharpDocument = document.GetCSharpDocument();
            Assert.NotNull(csharpDocument);

            var syntaxTree = document.GetSyntaxTree();
            Assert.NotNull(syntaxTree);
            Assert.True(syntaxTree.Options.DesignTimeMode);

            // Validate generated code.
            AssertCSharpDocumentMatchesBaseline(csharpDocument);

            var baselineFilename = Path.ChangeExtension(Filename, ".mappings.txt");
            var serializedMappings = LineMappingsSerializer.Serialize(csharpDocument, document.Source);

            if (GenerateBaselines)
            {
                var baselineFullPath = Path.Combine(TestProjectRoot, baselineFilename);
                File.WriteAllText(baselineFullPath, serializedMappings);
                return;
            }

            var testFile = new TestFile(baselineFilename, Assembly);
            if (!testFile.Exists())
            {
                throw new XunitException($"The resource {baselineFilename} was not found.");
            }

            var baseline = testFile.ReadAllText();

            // Normalize newlines to match those in the baseline.
            var actual = serializedMappings.Replace("\r", "").Replace("\n", "\r\n");

            Assert.Equal(baseline, actual);
        }
    }
}
