//
// Based on TemplateCodeTransformer.cs from Razor Generator (http://razorgenerator.codeplex.com/)
//     Licensed under the Microsoft Public License (MS-PL)
//
// Changes:
//     Author: Michael Hutchinson <mhutch@xamarin.com>
//     Copyright (c) 2012 Xamarin Inc (http://xamarin.com)
//     Licensed under the Microsoft Public License (MS-PL)
//

using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using System;
using RazorGenerator.Core;

namespace MonoDevelop.RazorGenerator
{
	class PreprocessedTemplateCodeTransformer : AggregateCodeTransformer
	{
		private static readonly IEnumerable<string> _defaultImports = new[] {
			"System",
			"System.Collections.Generic",
			"System.Linq",
			"System.Text"
		};

		private readonly RazorCodeTransformerBase[] _codeTransforms;

		public PreprocessedTemplateCodeTransformer ()
		{
			_codeTransforms = new RazorCodeTransformerBase[] {
				new SetImports(_defaultImports, replaceExisting: true),
				new AddGeneratedTemplateClassAttribute(),
				new ReplaceBaseType(),
				new SimplifyHelpers (),
				new FixMonoPragmas (),
			};
		}

		protected override IEnumerable<RazorCodeTransformerBase> CodeTransformers
		{
			get { return _codeTransforms; }
		}

		public override void ProcessGeneratedCode(CodeCompileUnit codeCompileUnit, CodeNamespace generatedNamespace, CodeTypeDeclaration generatedClass, CodeMemberMethod executeMethod)
		{
			base.ProcessGeneratedCode(codeCompileUnit, generatedNamespace, generatedClass, executeMethod);
			generatedClass.IsPartial = true;
			// The generated class has a constructor in there by default.
			generatedClass.Members.Remove(generatedClass.Members.OfType<CodeConstructor>().SingleOrDefault());
		}
	}

	class AddGeneratedTemplateClassAttribute : RazorCodeTransformerBase
	{
		public override void ProcessGeneratedCode (CodeCompileUnit codeCompileUnit, CodeNamespace generatedNamespace, CodeTypeDeclaration generatedClass, CodeMemberMethod executeMethod)
		{
			string tool = "RazorTemplatePreprocessor";
			Version version = GetType().Assembly.GetName().Version;
			generatedClass.CustomAttributes.Add(
				new CodeAttributeDeclaration(typeof(System.CodeDom.Compiler.GeneratedCodeAttribute).FullName,
					new CodeAttributeArgument(new CodePrimitiveExpression(tool)),
					new CodeAttributeArgument(new CodePrimitiveExpression(version.ToString()))
			));
		}
	}

	class ReplaceBaseType : RazorCodeTransformerBase
	{
		public override void Initialize (RazorHost razorHost)
		{
			razorHost.DefaultBaseClass = "System.Object";
		}

		bool hasBaseType;

		public override void ProcessGeneratedCode (CodeCompileUnit codeCompileUnit, CodeNamespace generatedNamespace, CodeTypeDeclaration generatedClass, CodeMemberMethod executeMethod)
		{
			hasBaseType = generatedClass.BaseTypes [0].BaseType != "System.Object";
			if (hasBaseType)
				return;

			executeMethod.Attributes = (executeMethod.Attributes & (~MemberAttributes.AccessMask | ~MemberAttributes.Override))
				| MemberAttributes.Private | MemberAttributes.Final;

			generatedClass.Members.Add (new CodeSnippetTypeMember (@"
        System.IO.TextWriter __razor_writer;

        private void WriteLiteral (string value)
        {
            __razor_writer.Write (value);
        }

        public string GenerateString ()
        {
            using (var sw = new System.IO.StringWriter ()) {
                Generate (sw);
                return sw.ToString();
	        }
        }

        public void Generate (System.IO.TextWriter writer)
        {
            this.__razor_writer = writer;
            Execute ();
            this.__razor_writer = null;
        }

        private void Write (object value)
        {
            __razor_writer.Write (System.Web.HttpUtility.HtmlEncode (value));
        }

        private void Write (Action<System.IO.TextWriter> write)
        {
            write (__razor_writer);
        }

        private static void WriteTo (System.IO.TextWriter writer, object value)
        {
            writer.Write (System.Web.HttpUtility.HtmlEncode (value));
        }

        private static void WriteLiteralTo (System.IO.TextWriter writer, string value)
        {
            writer.Write (value);
        }
        "));
		}
	}

	class FixMonoPragmas : RazorCodeTransformerBase
	{
		bool isMono = Type.GetType ("Mono.Runtime") != null;

		public override string ProcessOutput (string codeContent)
		{
			return isMono ? codeContent.Replace ("#line hidden", "#line hidden" + Environment.NewLine) : codeContent;
		}
	}

	class SimplifyHelpers : RazorCodeTransformerBase
	{
		/* rewrite:
		public System.Web.WebPages.HelperResult foo (int i)
		{
			return new System.Web.WebPages.HelperResult(__razor_helper_writer => {
				WriteLiteralTo(__razor_helper_writer, "<p>");
				WriteTo(__razor_helper_writer, i);
				WriteLiteralTo(__razor_helper_writer, "</p>\n");
			});
		}
		to:
		public static Action<TextWriter> foo (int i)
		{
			return __razor_helper_writer => {
				__razor_helper_writer.Write("<p>");
				__razor_helper_writer.Write(i);
				__razor_helper_writer.Write("</p>\n");
			};
		}
		*/

		static string[,] replacements = new string [,] {
			{ "public System.Web.WebPages.HelperResult " , "public static Action<System.IO.TextWriter> " },
			{ "return new System.Web.WebPages.HelperResult(__razor_helper_writer" , "return __razor_helper_writer" },
		};

		public override void ProcessGeneratedCode (CodeCompileUnit codeCompileUnit, CodeNamespace generatedNamespace, CodeTypeDeclaration generatedClass, CodeMemberMethod executeMethod)
		{
			foreach (var method in generatedClass.Members.OfType<CodeSnippetTypeMember> ()) {
				using (var writer = new System.IO.StringWriter (new System.Text.StringBuilder (method.Text.Length))) {
					using (var reader = new System.IO.StringReader (method.Text)) {
						bool lineHidden = false;
						string line;
						while ((line = reader.ReadLine ()) != null) {
							if (line.StartsWith ("#line")) {
								lineHidden = line == "#line hidden";
							}
							if (lineHidden && line == "});") {
								writer.WriteLine ("};");
								continue;
							}
							var len = replacements.GetLength (0);
							for (int i = 0; i < len; i++) {
								var bad = replacements[i,0];
								if (line.StartsWith (bad)) {
									line = replacements[i,1] + line.Substring (bad.Length);
								}
							}
							writer.WriteLine (line);
						}
					}
					method.Text = writer.ToString ();
				}
			}
		}
	}
}