// FrontEndArgs describes a class that manages all of the command-line arguments 
// used to create the datatrace file. It is created by ProfilerLauncher using
// the arguments gathered from there, and is handed to the Profiler, Declaration
// Printer, and VariableVisitor. The implementation approach is a dictionary from daikon args
// to value supplied on the command line.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;

namespace DotNetFrontEnd
{
  /// <summary>
  /// Manages all command-line arguments to generate the dtrace file.
  /// </summary>
  [Serializable]
  public class FrontEndArgs
  {
    #region Constants

    /// <summary>
    /// The folder the datatrace file will be printed to
    /// </summary>
    private const string DatatraceOutputFolder = "daikon-output/";

    /// <summary>
    /// The extension for the dtrace file
    /// </summary>
    private const string DatatraceExtension = ".dtrace";

    /// <summary>
    /// The extension for the declaration portion of a datatrace if it's a separate file.
    /// </summary>
    private const string DeclarationFileExtension = ".decls";

    /// <summary>
    /// Max nesting depth to be used if none is provided
    /// </summary>
    private const int DefaultNestingDepth = 2;

    /// <summary>
    /// Whether to enter verbose mode if the user doesn't specify true or false
    /// TODO(#28): Change before release
    /// </summary>
    private const bool DefaultVerboseMode = true;

    /// <summary>
    /// The location a user should specify to print to standard out.
    /// </summary>
    private const string PrintOutputFileLocation = "STDOUT";

    /// <summary>
    /// The location to save the program to, if the user specifies that but does not specify an 
    /// alternate save location.
    /// </summary>
    private const string DefaultSaveProgramLocation = "InstrumentedProgram.exe";

    /// <summary>
    /// The number to returned when sample start is gotten if no sample start is in use.
    /// </summary>
    public const int NoSampleStart = -1;

    /// <summary>
    /// Lambdas expressions create objects with special characters in the name -- don't print these
    /// </summary>
    private Regex PptAlwaysExclude;

    #endregion

    /// <summary>
    /// Listing of possible arguments. See Daikon Doc for explanations.
    /// Violates standard naming convention for ease of parsing.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming",
      "CA1709:IdentifiersShouldBeCasedCorrectly")]
    internal enum PossibleArgument
    {
      // Comments indicate section argument is defined under in documentation
      /* */
      // Program Point Options
      ppt_omit_pattern,
      ppt_select_pattern,
      sample_start,
      // Variables options
      arrays_only,
      is_readonly_flags,    // Flag in development
      is_enum_flags,        // Flag in development
      is_property_flags,    // Flag in development
      nesting_depth,
      omit_var,
      omit_dec_type,
      omit_parent_dec_type,
      purity_file,
      std_visibility,
      // Misc. options
      comparability,
      assembly_name,
      dont_catch_exceptions,
      dtrace_append,
      enum_underlying_values,
      force_unix_newline,
      friendly_dec_types,
      linked_lists,
      output_location,
      save_and_run,
      save_program,
      portable_dll, 
      verbose,
      wpf,
      // Not an option -- the location of the program to be profiled
      assembly_location,
    }

    /// <summary>
    /// Whether or not overriding existing arguments is allowed when adding new ones
    /// </summary>
    private enum AllowOverride
    {
      Allow,
      DontAllow,
    }

    /// <summary>
    /// The representation of the arguments handed to the program
    /// </summary>
    private Dictionary<PossibleArgument, string> programArguments;

    /// <summary>
    /// String holding the arguments that created the instance
    /// </summary>
    private string argsToWrite;

    /// <summary>
    /// The index in the given argument list of the first non-front end argument (the name of the 
    /// program to be profiled and its arguments)
    /// </summary>
    public int ProgramArgIndex { get; private set; }

    /// <summary>
    /// Create a new front end args representation based off given string[] of command-line
    /// arguments
    /// </summary>
    /// <param name="args">The command-line arguments, as seen by the program</param>
    public FrontEndArgs(string[] args)
    {
      if (args == null)
      {
        throw new ArgumentNullException("args");
      }
      this.argsToWrite = String.Join(" ", args);
      this.programArguments = new Dictionary<PossibleArgument, string>();
      this.ProgramArgIndex = 0;
      this.PptAlwaysExclude = new Regex("<>");
      this.PopulateDefaultArguments();

      // Used to allow enumeration of PossibleArgument
      foreach (string currentArg in args)
      {
        if (currentArg.StartsWith("--", StringComparison.OrdinalIgnoreCase))
        {
          string[] pair = currentArg.Split('=');
          if (pair.Length != 1 && pair.Length != 2)
          {
            throw new ArgumentException("Cannot process argument: " + currentArg
                + ", it has too many = characters.");
          }
          // If the user specifies an argument twice accept the second version
          foreach (var enumVal in Enum.GetValues(typeof(PossibleArgument)))
          {
            // Remove the -- at the beginning of the argument then convert to enum type 
            // formatting
            string keyStr = ChangeArgKeyToEnumType(pair[0].Substring(2));
            if (keyStr == enumVal.ToString())
            {
              PossibleArgument enumKey;
              if (!Enum.TryParse<PossibleArgument>(keyStr, out enumKey))
              {
                throw new InvalidDataException("Unable to parse " + keyStr + " as an argument.");
              }
              if (this.programArguments.ContainsKey(enumKey))
              {
                this.programArguments.Remove(enumKey);
              }

              if (pair.Length < 2)
              {
                this.AddArgument(enumKey, null);
              }
              else
              {
                this.programArguments.Add(enumKey, pair[1]);
              }
            }
          }
          // We processed a valid arg, so increment the index by one
          this.ProgramArgIndex++;
        }
        else
        {
          // Stop considering args after the first non-relevant arg, we are now at the index of the
          // program to be profiled.
          break;
        }
      }
      string programPath = args[this.ProgramArgIndex];
      this.AddArgument(PossibleArgument.assembly_location, programPath);

      string assemblyName = ExtractAssemblyNameFromProgramPath(programPath);

      this.AddArgument(PossibleArgument.assembly_name, assemblyName);

      // If they specified an output location earlier don't override it
      if (!IsArgumentSpecified(PossibleArgument.output_location))
      {
        this.AddArgument(PossibleArgument.output_location,
            DatatraceOutputFolder + assemblyName + DatatraceExtension);
      }

      this.PurityMethods = new List<string>();
      if (IsArgumentSpecified(PossibleArgument.purity_file))
      {
        this.LoadPurityFile();
      }

      // This needs to occur after assembly path has been set.
      if (args.Contains("--wpf"))
      {
        File.Copy(this.AssemblyPath, this.AssemblyPath + ".tmp", true);
        this.AddArgument(PossibleArgument.save_program, this.AssemblyPath);
        this.AddArgument(PossibleArgument.assembly_location, this.AssemblyPath + ".tmp");
        this.AddArgument(PossibleArgument.save_and_run, true.ToString());
      }

      this.PrintOutput = (this.OutputLocation == PrintOutputFileLocation);
    }

    #region Methods For Handling Command Line Args

    /// <summary>
    /// Add the default arguments. Called before any arguments have been added.
    /// </summary>
    private void PopulateDefaultArguments()
    {
      // From the chicory specification
      this.programArguments.Add(PossibleArgument.nesting_depth,
          DefaultNestingDepth.ToString(CultureInfo.InvariantCulture));

      // TODO(#28): Remove before release.
      this.programArguments.Add(PossibleArgument.verbose, DefaultVerboseMode.ToString());

      // TODO(#30): This is true for chicory, so should be true for us as well.
      // this.programArguments.Add(PossibleArgument.linked_lists, false.ToString());

      // TODO(#43): Change this back to true.
      this.programArguments.Add(PossibleArgument.arrays_only, false.ToString());

      // TODO(#41): Set to false before release.
      this.programArguments.Add(PossibleArgument.dont_catch_exceptions, true.ToString());

      this.programArguments.Add(PossibleArgument.friendly_dec_types, "false");
    }

    /// <summary>
    /// Extract the name of the assembly from the program path.
    /// </summary>
    /// <param name="programPath">The location of the executable</param>
    /// <returns>Name of the assembly to target</returns>
    /// Warning suppressed because we need to load the assembly for type resolution.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability",
        "CA2001:AvoidCallingProblematicMethods", MessageId = "System.Reflection.Assembly.LoadFrom")]
    private static string ExtractAssemblyNameFromProgramPath(string programPath)
    {
      Assembly programAssembly = System.Reflection.Assembly.LoadFrom(programPath);
      string assemblyName = programAssembly.FullName;
      // This will be the display name, we are interested in the part before the comma
      return assemblyName.Substring(0, assemblyName.IndexOf(','));
    }

    /// <summary>
    /// Changes the key of a argument from command line form to match the type of the enum.
    /// </summary>
    /// <param name="p">The key of the argument, from the command line</param>
    /// <returns>Version of the argument that can be matched to the enum's ToString</returns>
    private static string ChangeArgKeyToEnumType(string p)
    {
      // Command line version has - in it, this isn't allowed in a .NET type name, so we use
      // underscores in place.
      return p.Replace('-', '_');
    }

    /// <summary>
    /// Add a given argument key-value pair
    /// </summary>
    /// <param name="argumentKey">The type of argument to add</param>
    /// <param name="argumentValue">The value of the argument to add</param>
    /// <returns>Previous argument value if any, otherwise null</returns>
    private string AddArgument(PossibleArgument argumentKey, string argumentValue)
    {
      // Special case -- there is a default value for save-location but should only
      // be set if and only if the option is supplied with no value.
      if (argumentKey == PossibleArgument.save_program && argumentValue == null)
      {
        argumentValue = FrontEndArgs.DefaultSaveProgramLocation;
      }
      string oldVal = null;
      if (this.programArguments.ContainsKey(argumentKey))
      {
        this.programArguments.TryGetValue(argumentKey, out oldVal);
        this.programArguments.Remove(argumentKey);
      }
      this.programArguments.Add(argumentKey, argumentValue);
      return oldVal;
    }

    /// <summary>
    /// Whether the given argument was specified
    /// </summary>
    /// <param name="possibleArg">The argument type to check</param>
    /// <returns>True if the argument was specified, otherwise false</returns>
    private bool IsArgumentSpecified(PossibleArgument possibleArg)
    {
      return this.programArguments.ContainsKey(possibleArg);
    }

    /// <summary>
    /// Tries to get the value of the given program argument
    /// </summary>
    /// <param name="possibleArg">The program to get the argument of</param>
    /// <param name="value">The value of the argument if it exists, otherwise null</param>
    /// <returns>The argument's value if it has been specified, otherwise null</returns>
    private bool TryGetArgumentValue(PossibleArgument possibleArg, out string value)
    {
      return this.programArguments.TryGetValue(possibleArg, out value);
    }

    /// <summary>
    /// Load the purity file and extract the list of methods
    /// </summary>
    private void LoadPurityFile()
    {
      StreamReader f = File.OpenText(this.PurityFile);
      try
      {
        while (!f.EndOfStream)
        {
          this.PurityMethods.Add(f.ReadLine());
        }
      }
      finally
      {
        f.Close();
      }
    }

    #endregion

    #region Helper Methods for VariableVisitor / Visitor

    /// <summary>
    /// Returns whether the given variable should be printed, considering possible include 
    /// and exclude patterns.
    /// The name is checked against omit and select patterns if provided.
    /// If the name matches the select pattern and does not match the omit pattern it is 
    /// printed. Tests are skipped if a pattern was not supplied.
    /// </summary>
    /// <param name="varName">Name of the variable to print</param>
    /// <returns>True if the variable should be printed, false otherwise</returns>
    public bool ShouldPrintVariable(string varName)
    {
      // value__ is an extra field added describing enum values.    
      if (varName.EndsWith("value__"))
      {
        return false;
      }
      return this.OmitVar == null || !this.OmitVar.IsMatch(varName);
    }

    /// <summary>
    /// Returns whether the given program point should be printed, considering possible include
    /// and exclude patterns.        
    /// The name is checked against omit and select patterns if provided.
    /// If the name matches the select pattern and does not match the omit pattern it is 
    /// printed. Tests are skipped if a pattern was not supplied.
    /// </summary>
    /// <param name="programPointName">Name of the program point to print</param>
    /// <returns>True if the program point should be printed, false otherwise</returns>
    public bool ShouldPrintProgramPoint(string programPointName)
    {
      return ShouldPrintProgramPoint(programPointName, String.Empty);
    }

    /// <summary>
    /// Returns whether the given program point should be printed, considering possible include
    /// and exclude patterns.
    /// The name is checked against omit and select patterns if provided.
    /// If the name matches the select pattern and does not match the omit pattern it is 
    /// printed. Tests are skipped if a pattern was not supplied.
    /// </summary>
    /// <param name="programPointName">Name of the program point to print</param>
    /// <param name="label">Optional data that may be appended to end of method call</param>
    /// <returns>True if the program point should be printed, false otherwise</returns>
    public bool ShouldPrintProgramPoint(string programPointName, string label)
    {
      bool passSelectTest = true;
      bool passOmitTest = true;

      if (this.PptOmitPattern != null)
      {
        passOmitTest = !this.PptOmitPattern.IsMatch(programPointName + label);
      }
      if (this.PptSelectPattern != null)
      {
        passSelectTest = this.PptSelectPattern.IsMatch(programPointName + label);
      }

      return !this.PptAlwaysExclude.IsMatch(programPointName + label) &&
          passOmitTest && passSelectTest;
    }

    /// <summary>
    /// Get the appropriate binding flags for field inspection, without specifying static or instance.
    /// </summary>
    /// <param name="type">Type to inspect</param>
    /// <returns>Binding flag specifying visibility of fields to inspect</returns>
    private System.Reflection.BindingFlags GetAccessOptionsForFieldInspection(Type type, 
	      Type originatingType)
    {
      if (type == null)
      {
        throw new ArgumentNullException("type");
      }

      var memberAccessOptionToUse = this.BaseMemberAccessOptions;
      if (type.AssemblyQualifiedName != null && 
          type.AssemblyQualifiedName.Equals(originatingType.AssemblyQualifiedName))
      {
        memberAccessOptionToUse |= BindingFlags.NonPublic;
      }
      
      // We don't want the internal fields of System objects
      // Assumes that the Assembly of StringType and the Assembly of HashSetType are the Assemblies
      // that we want to exclude.
      return memberAccessOptionToUse & (
             (TypeManager.StringType.Assembly.Equals(type.Assembly)
           || TypeManager.HashSetType.Assembly.Equals(type.Assembly)) ?
        ~System.Reflection.BindingFlags.NonPublic : memberAccessOptionToUse);
    }

    /// <summary>
    /// Get the appropriate binding flags for field inspection of instance variables only
    /// </summary>
    /// <param name="type">Type to inspect</param>
    /// <returns>Binding flag specifying visibility of fields to inspect</returns>
    public BindingFlags GetInstanceAccessOptionsForFieldInspection(Type type, Type originatingType)
    {
      return BindingFlags.Instance | this.GetAccessOptionsForFieldInspection(type, originatingType);
    }

    /// <summary>
    /// Get the appropriate binding flags for field inspection of static variables only
    /// </summary>
    /// <param name="type">Type to inspect</param>
    /// <returns>Binding flag specifying visibility of fields to inspect</returns>
    public BindingFlags GetStaticAccessOptionsForFieldInspection(Type type, 
	Type originatingType)
    {
      return BindingFlags.Static | this.GetAccessOptionsForFieldInspection(type, 
	originatingType);
    }

    #endregion

    #region Getters for Reflection Args

    // See daikon documentation for the precise meaning of these arguments.
    // TODO(#26): Add the URL here.

    /// <summary>
    /// The name of the assembly the program is executing on
    /// </summary>
    public string AssemblyName
    {
      // Required arg
      get { return this.programArguments[PossibleArgument.assembly_name]; }
    }

    /// <summary>
    /// The path to the current assembly
    /// </summary>
    public string AssemblyPath
    {
      // Required arg
      get { return this.programArguments[PossibleArgument.assembly_location]; }
    }

    /// <summary>
    /// Regex describing variables to omit.
    /// </summary>
    public Regex OmitVar
    {
      // Optional arg
      get
      {
        string val;
        this.TryGetArgumentValue(PossibleArgument.omit_var, out val);
        return (val == null) ? null : new Regex(val);
      }
    }

    /// <summary>
    /// Regex describing variables to omit.
    /// </summary>
    public Regex OmitDecType
    {
        // Optional arg
        get
        {
            string val;
            this.TryGetArgumentValue(PossibleArgument.omit_dec_type, out val);
            return (val == null) ? null : new Regex(val); 
        }
    }

    /// <summary>
    /// Regex describing variables to omit.
    /// </summary>
    public Regex OmitParentDecType
    {
        // Optional arg
        get
        {
            string val;
            this.TryGetArgumentValue(PossibleArgument.omit_parent_dec_type, out val);
            return (val == null) ? null : new Regex(val);
        }
    }

    /// <summary>
    /// Regex describing program points to omit.
    /// </summary>
    public Regex PptOmitPattern
    {
      // Optional arg
      get
      {
        string val;
        this.TryGetArgumentValue(PossibleArgument.ppt_omit_pattern, out val);
        return (val == null) ? null : new Regex(val);
      }
    }

    /// <summary>
    /// Regex describing the only program points to select, or null to select all.
    /// </summary>
    public Regex PptSelectPattern
    {
      // Optional arg
      get
      {
        string val;
        this.TryGetArgumentValue(PossibleArgument.ppt_select_pattern, out val);
        return (val == null) ? null : new Regex(val);
      }
    }

    /// <summary>
    /// Where to output the datatrace and decls
    /// </summary>
    public string OutputLocation
    {
      // Required arg (has default)
      get
      {
        return this.programArguments[PossibleArgument.output_location];
      }
    }

    /// <summary>
    /// The maximum level of inspection to visit (e.g. a is 1, a.a is 2, a.a.a is 3, etc.)
    /// </summary>
    public int MaxNestingDepth
    {
      // Required arg (has default)
      get
      {
        return int.Parse(this.programArguments[PossibleArgument.nesting_depth],
            CultureInfo.InvariantCulture);
      }
    }

    /// <summary>
    /// Whether the output files should require '\n' instead of the Environment standard newline
    /// </summary>
    public bool ForceUnixNewLine
    {
      // Optional arg
      get { return this.IsArgumentSpecified(PossibleArgument.force_unix_newline); }
    }

    /// <summary>
    /// Whether to limit the output fields to those visible at the given program point.
    /// </summary>
    public bool StdVisibility
    {
      get { return this.IsArgumentSpecified(PossibleArgument.std_visibility); }
    }

    /// <summary>
    /// Defines which members to visit
    /// </summary>
    private System.Reflection.BindingFlags BaseMemberAccessOptions
    {
      get
      {
        // Access everything unless std_visibility, then don't access non public
        return
            System.Reflection.BindingFlags.Public |
            (this.IsArgumentSpecified(PossibleArgument.std_visibility) ?
                0 : System.Reflection.BindingFlags.NonPublic);
      }
    }

    public BindingFlags InstanceMemberAccessOptions
    {
      get { return BaseMemberAccessOptions | BindingFlags.Instance; }
    }

    public BindingFlags StaticMemberAccessOptions
    {
      get { return BaseMemberAccessOptions | BindingFlags.Static; }
    }

    /// <summary>
    /// If the program with instrumentation calls added should be saved to disk, then the path
    /// the program should be saved at, otherwise null, in which case the program will be executed
    /// immediately.
    /// </summary>
    public string SaveProgram
    {
      get
      {
        string result;
        this.programArguments.TryGetValue(PossibleArgument.save_program, out result);
        return result;
      }
    }

    /// <summary>
    /// Whether to perform a comparability calculation
    /// </summary>
    public bool StaticComparability
    {
        get { return this.IsArgumentSpecified(PossibleArgument.comparability); }
    }

    /// <summary>
    /// Whether to append to the existing dtrace, rather than overwrite.
    /// </summary>
    public bool DtraceAppend
    {
      get { return this.IsArgumentSpecified(PossibleArgument.dtrace_append); }
    }

    /// <summary>
    /// Whether to print the integral-type values for enum values, rather than the value's name
    /// </summary>
    public bool EnumUnderlyingValues
    {
      get { return this.IsArgumentSpecified(PossibleArgument.enum_underlying_values); }
    }

    /// <summary>
    /// Whether to print status messages and debug information.
    /// </summary>
    public bool VerboseMode
    {
      get { return bool.Parse(this.programArguments[PossibleArgument.verbose]); }
    }

    /// <summary>
    /// Variable to reduce printing of a program point
    /// </summary>
    public int SampleStart
    {
      get
      {
        return (this.programArguments.ContainsKey(PossibleArgument.sample_start) ?
                int.Parse(this.programArguments[PossibleArgument.sample_start],
                    CultureInfo.InvariantCulture) :
                FrontEndArgs.NoSampleStart);
      }
    }

    /// <summary>
    /// Whether to interpret class that are of linked list type (they have a single field of their
    /// own type) as arrays.
    /// </summary>
    public bool LinkedLists
    {
      get { return this.programArguments.ContainsKey(PossibleArgument.linked_lists); }
    }

    /// <summary>
    /// Not a standard arg, whether to print the output to stdout when finished
    /// </summary>
    public bool PrintOutput { get; private set; }

    /// <summary>
    /// To perform element inspect on arrays only -- not on list implementors
    /// </summary>
    public bool ElementInspectArraysOnly
    {
      get { return bool.Parse(this.programArguments[PossibleArgument.arrays_only]); }
    }

    /// <summary>
    /// File specifying methods considered to be pure -- they will be evaluated and listed as 
    /// fields
    /// </summary>
    public string PurityFile
    {
      get
      {
        string result;
        this.programArguments.TryGetValue(PossibleArgument.purity_file, out result);
        return result;
      }
    }

    /// <summary>
    /// If no custom exception handling should occur -- allows use of debugger but gives less 
    /// friendly error messages.
    /// </summary>
    public bool DontCatchExceptions
    {
      get
      {
        return (this.programArguments.ContainsKey(PossibleArgument.dont_catch_exceptions) ?
           bool.Parse(this.programArguments[PossibleArgument.dont_catch_exceptions]) : false);
      }
    }

    /// <summary>
    /// List of which methods should be considered Pure and called at program points
    /// </summary>
    internal List<string> PurityMethods
    {
      get;
      private set;
    }

    /// <summary>
    /// Whether dec-types should be printed in the style they are typed in as.
    /// If false we print assembly qualified names, e.g. Foo`1[System.Int]
    /// If true we print Foo&lt;Int&gt; 
    /// </summary>
    public bool FriendlyDecTypes
    {
      get { return bool.Parse(this.programArguments[PossibleArgument.friendly_dec_types]); }
    }

    /// <summary>
    /// Whether the program should be saved to disk and immediately executed
    /// </summary>
    public bool SaveAndRun
    {
      get { return this.programArguments.ContainsKey(PossibleArgument.save_and_run); }
    }

    public bool WPF
    {
      get { return this.programArguments.ContainsKey(PossibleArgument.wpf); }
    }

    public bool IsPortableDll
    {
        get { return this.programArguments.ContainsKey(PossibleArgument.portable_dll); }
    }

    /// <summary>
    /// Whether to print the is_property flag for Properties. Not compatible
    /// with old versions of Daikon.
    /// </summary>
    public bool IsPropertyFlags
    {
      get { return this.programArguments.ContainsKey(PossibleArgument.is_property_flags); }
    }

    /// <summary>
    /// Whether to print the is_readonly flag for read only variables. Not compatible
    /// with old versions of Daikon.
    /// </summary>
    public bool IsReadOnlyFlags
    {
        get { return this.programArguments.ContainsKey(PossibleArgument.is_readonly_flags); }
    }

    /// <summary>
    /// Whether to print the is_Enum flag for Enums. Not compatible
    /// with old versions of Daikon.
    /// </summary>
    public bool IsEnumFlags
    {
      get { return this.programArguments.ContainsKey(PossibleArgument.is_enum_flags); }
    }

    #endregion

    /// <summary>
    /// Set the extension on the output location to be the one for a declaration file
    /// </summary>
    public void SetDeclExtension()
    {
      if (!this.PrintOutput)
      {
        this.AddArgument(PossibleArgument.output_location, Path.ChangeExtension(this.OutputLocation,
            DeclarationFileExtension));
      }
    }

    /// <summary>
    /// Set the extension on the output location to be the one for a dtrace file
    /// </summary>
    public void SetDtraceExtension()
    {
      if (!this.PrintOutput)
      {
        this.AddArgument(PossibleArgument.output_location, Path.ChangeExtension(this.OutputLocation,
            DatatraceExtension));
      }
    }

    /// <summary>
    /// Get the original arguments that were used to construct the instance
    /// </summary>
    /// <returns>Space separated string of the original arguments</returns>
    public string GetArgsToWrite
    {
      get
      {
        return this.argsToWrite;
      }
    }
  }
}
