/** RatTerm is an immutable record type representing a term in a
    polynomial expression.  The term has the form coeff * x^expt.
    <p>

    A RatTerm can be notated by the pair (C . E),
    where C is the string representation of t.coeff, and E is t.expt.
    <p>

    For a given RatTerm r, "coefficient of r" is synonymous with
    r.coeff, and likewise "exponent of r" is synonymous with r.expt.
    <p>

    (1 . 3), (3/4 . 17), (7/2 . -1), and (NaN . 74) are all valid
    RatTerms, corresponding to the polynomial terms "x^3", "3/4*x^17",
    "7/2*x^-1" and "NaN*x^74", respectively.
*/
public class RatTerm {
    /** coefficient of this term. */
    public RatNum coeff;

    /** exponent of this term. */
    public int expt;

    /** @requires: c != null
        @effects:
        constructs a new RatTerm, t, with t.coeff = c and t.expt = e.
    */
    public RatTerm(RatNum c, int e) {
        coeff = c;
        expt = e;
    }

    /** Standard equality operation.
        @return true iff 'obj' is an instance of a RatTerm and 'this' = 'obj'
    */
    public override bool Equals(object obj)
    {
        if (obj is RatTerm) {
            RatTerm rt = (RatTerm) obj;
            return this.expt == rt.expt && this.coeff.Equals(rt.coeff);
        } else {
            return false;
        }
    }

    public override int GetHashCode() 
    {
      return this.expt + this.coeff.GetHashCode();
    }

    /** @return implementation specific debugging string. */
    public string debugPrint() {
        return "Term<coeff:"+this.coeff.unparse()+" expt:"+this.expt+">";
    }

    public string toString() { return debugPrint(); }
}
