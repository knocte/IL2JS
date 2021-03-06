﻿//
// Instruct IL2JS compiler to break when compiling definition (for debugging of compiler only!)
// NOTE: Appears in both IL2JS's mscorlib and CLR's JSTypes assemblies
//

using System;

namespace Microsoft.LiveLabs.JavaScript.IL2JS
{
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Constructor | AttributeTargets.Delegate | AttributeTargets.Enum | AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Event)]
    public class InlineAttribute : Attribute
    {
        public bool IsInlined { get; protected set; }

        public InlineAttribute(bool isInlined)
        {
            IsInlined = isInlined;
        }
    }
}