// VariableVisitorException defines an exception that will be thrown by the reflective visitor 
// common call point. First any exception that occurred during the visiting will be caught. In this 
// way we can differentiate between an exception that occur's in our code from one that occur's in 
// the program.

namespace Celeriac
{
  using System;

  /// <summary>
  /// Exception to capture any exception thrown during reflective visiting
  /// Suppresses warnings because we only want to craft specific instances of this exception.
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design",
      "CA1032:ImplementStandardExceptionConstructors")]
  [Serializable]
  public class VariableVisitorException : Exception
  {
    /// <summary>
    /// Message to be associated with an exception of this type.
    /// </summary>
    private static readonly string message = "An error occurred during reflective visiting.";

    /// <summary>
    /// Create a new instance of VariableVisitorException. Preseve the thrown exception for 
    /// debugging.
    /// </summary>
    /// <param name="baseException">Exception thrown during reflective visiting.</param>
    public VariableVisitorException(Exception baseException) : base(message, baseException) { }
  }
}
