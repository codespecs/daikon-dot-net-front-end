using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Cci;
using System.IO;
using System.Diagnostics.Contracts;

namespace Celeriac
{
  public class PortableHost : MetadataReaderHost
  {
    /// <summary>
    /// The PeReader instance that is used to implement LoadUnitFrom.
    /// </summary>
    private readonly PeReader peReader;

    private AssemblyIdentity/*?*/ coreAssemblySymbolicIdentity;

    [ContractInvariantMethod]
    private void ObjectInvariants()
    {
      Contract.Invariant(peReader != null);
    }

    /// <summary>x
    /// Allocates a simple host environment using default settings inherited from MetadataReaderHost and that
    /// uses PeReader as its metadata reader.
    /// </summary>
    public PortableHost()
      : base(new NameTable(), new InternFactory(), 0, null, true)
    {
      this.peReader = new PeReader(this);
    }

    /// <summary>
    /// Allocates a simple host environment using default settings inherited from MetadataReaderHost and that
    /// uses PeReader as its metadata reader.
    /// </summary>
    /// <param name="nameTable">
    /// A collection of IName instances that represent names that are commonly used during compilation.
    /// This is a provided as a parameter to the host environment in order to allow more than one host
    /// environment to co-exist while agreeing on how to map strings to IName instances.
    /// </param>
    public PortableHost(INameTable nameTable)
      : base(nameTable, new InternFactory(), 0, null, false)
    {

      Contract.Requires(nameTable != null);
      Contract.Ensures(base.NameTable == nameTable);

      this.peReader = new PeReader(this);
    }

    /// <summary>
    /// Returns the unit that is stored at the given location, or a dummy unit if no unit exists at that location or if the unit at that location is not accessible.
    /// </summary>
    /// <param name="location">A path to the file that contains the unit of metdata to load.</param>
    public override IUnit LoadUnitFrom(string location)
    {
      IUnit result = this.peReader.OpenModule(
        BinaryDocument.GetBinaryDocumentForFile(location, this));
      this.RegisterAsLatest(result);
      return result;
    }

    public override AssemblyIdentity UnifyAssembly(IAssemblyReference assemblyReference)
    {
      return this.UnifyAssembly(assemblyReference.AssemblyIdentity);
    }

    /// <summary>
    /// Default implementation of UnifyAssembly. Override this method to change the behavior.
    /// </summary>
    public override AssemblyIdentity UnifyAssembly(AssemblyIdentity assemblyIdentity)
    {
      if (assemblyIdentity.Name.UniqueKeyIgnoringCase == this.CoreAssemblySymbolicIdentity.Name.UniqueKeyIgnoringCase &&
        assemblyIdentity.Culture == this.CoreAssemblySymbolicIdentity.Culture &&
        IteratorHelper.EnumerablesAreEqual(assemblyIdentity.PublicKeyToken, this.CoreAssemblySymbolicIdentity.PublicKeyToken))
        return this.CoreAssemblySymbolicIdentity;
      if (this.CoreIdentities.Contains(assemblyIdentity)) return this.CoreAssemblySymbolicIdentity;
      return assemblyIdentity;
    }


    /// <summary>
    /// The identity of the assembly containing the core system types such as System.Object.
    /// </summary>
    public new AssemblyIdentity CoreAssemblySymbolicIdentity
    {
      get
      {
        Contract.Ensures(Contract.Result<AssemblyIdentity>() != null);

        if (this.coreAssemblySymbolicIdentity == null)
        {
          var path = @"C:\Program Files\Reference Assemblies\Microsoft\Framework\.NETPortable\v4.5\Profile\Profile7\mscorlib.dll";
          var assembly = this.LoadUnitFrom(path) as IAssembly;
          this.coreAssemblySymbolicIdentity = assembly.AssemblyIdentity;
        }

        return this.coreAssemblySymbolicIdentity;
      }
    }
  }
}
