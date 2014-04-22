﻿namespace Microsoft.VisualStudio.Composition.EmbeddedTypeReceiver
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.Shell.Interop;
    using MefV1 = System.ComponentModel.Composition;
    using MefV2 = System.Composition;


    // When these two using directives are switched in tandem with those
    // found in the PartThatImportsLazyOfEmbeddedType.cs source file,
    // one can verify that behaviors work with non-embeddable types
    // yet may break with embeddable ones.
    ////using TEmbedded = System.IDisposable;
    using TEmbedded = Microsoft.VisualStudio.Shell.Interop.IVsRetargetProjectAsync;

    /// <summary>
    /// The type must appear in a different assembly from the exporting part
    /// so that the two assemblies have distinct Type instances for the embeddable interface.
    /// </summary>
    [MefV1.Export, MefV2.Export]
    public class PartThatImportsLazyOfEmbeddedType
    {
        [MefV1.Import, MefV2.Import]
        public Lazy<TEmbedded> RetargetProject { get; set; }

        public TEmbedded RetargetProjectNoLazy
        {
            get { return this.RetargetProject.Value; }
        }
    }

    [MefV1.Export, MefV2.Export]
    public class PartThatImportsLazyOfEmbeddedTypeNonPublic
    {
        [MefV1.Import, MefV2.Import]
        internal Lazy<TEmbedded> RetargetProject { get; set; }

        public TEmbedded RetargetProjectNoLazy
        {
            get { return this.RetargetProject.Value; }
        }
    }

    [MefV1.Export, MefV2.Export]
    public class PartThatImportsEmbeddedType
    {
        [MefV1.Import, MefV2.Import]
        public TEmbedded RetargetProject { get; set; }
    }
}
