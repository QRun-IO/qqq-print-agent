/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Reflection;
using Qqq.PrintAgent.Presentation.Service;

namespace Qqq.PrintAgent.Presentation.Tests;

/// <summary>The rule that makes the presentation assembly testable: it, and the product assemblies it builds on, reference no windowing library.</summary>
public sealed class ArchitectureTests
{
   private static readonly string[] s_windowing = ["Microsoft.WinUI", "Microsoft.WindowsAppRuntime", "Microsoft.WindowsAppSDK", "Microsoft.InteractiveExperiences", "H.NotifyIcon", "KofTwentyTwo.AppKit.WinUI"];



   /// <summary>
   /// Walks the references of the presentation assembly and of every product assembly reached from it, and finds none of
   /// WinUI, the Windows App SDK (its runtime or any of its projection assemblies), H.NotifyIcon or AppKit.WinUI. A view type cannot leak into the assembly without one of
   /// those references, so this keeps "UI-free" true after the project file comment is forgotten. The walk is checked to
   /// have reached the Windows and Core layers, so an empty walk cannot pass.
   /// </summary>
   [Fact]
   public void PresentationAssemblyAndWhatItBuildsOn_ReferenceNoWindowingLibrary()
   {
      Dictionary<string, Assembly> product = new(StringComparer.Ordinal);
      List<string> references = [];
      Queue<Assembly> pending = new([typeof(ServiceClient).Assembly]);
      while(pending.TryDequeue(out Assembly? assembly))
      {
         if(!product.TryAdd(assembly.GetName().Name!, assembly))
         {
            continue;
         }
         foreach(AssemblyName reference in assembly.GetReferencedAssemblies())
         {
            references.Add(reference.Name!);
            if(reference.Name!.StartsWith("Qqq.PrintAgent", StringComparison.Ordinal))
            {
               pending.Enqueue(Assembly.Load(reference));
            }
         }
      }

      Assert.Equal("Qqq.PrintAgent.Core,Qqq.PrintAgent.Presentation,Qqq.PrintAgent.Windows", string.Join(',', product.Keys.Order(StringComparer.Ordinal)));
      Assert.Contains("CommunityToolkit.Mvvm", references, StringComparer.Ordinal);
      Assert.DoesNotContain(references, reference => s_windowing.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)) || reference.EndsWith(".Projection", StringComparison.Ordinal));
   }
}
