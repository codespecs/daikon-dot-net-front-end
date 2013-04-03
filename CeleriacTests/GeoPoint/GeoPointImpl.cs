/**
 * A GeoPoint models a point on the earth.<p>
 *
 * @specfield  latitude : double          // measured in degrees
 * @specfield  longitude : double         // measured in degrees
 * @endspec
 *
 * <p>GeoPoints are immutable.
 *
 *
 * <p>South latitudes and west longitudes are represented by negative numbers.
 *
 * <p>The code may assume that the represented points are nearby Boston.
 *
 * <p><b>Implementation hint</b>:<br>
 * Boston is at approximately 42 deg. 21 min. 30 sec. N latitude and 71
 * deg. 03 min. 37 sec. W longitude.  At that location, there are
 * approximately 69.023 miles per degree of latitude and 51.075 miles per
 * degree of longitude.  An implementation may use these values when
 * determining distances and headings.
 **/
namespace GeoPoint
{
    public class GeoPoint
    {
        private static double REP_SCALE_FACTOR = 1000000.0;

        private int latitude;
        private int longitude;

        // Constructors

        /**
         * @requires the point given by (latitude, longitude) is near Boston
         * @effects constructs a GeoPoint from a latitude and longitude given in degrees East and North.
         **/
        public GeoPoint(int latitude, int longitude)
        {
            Assert.assertTrue((41000000 <= latitude) && (latitude <= 43000000));
            Assert.assertTrue((-72000000 <= longitude) && (longitude <= -70000000));

            this.latitude = latitude;
            this.longitude = longitude;
        }
        // Observers

        public override string ToString()
        {
            return "Pt{" +
              (latitude / REP_SCALE_FACTOR) +
              "," +
              (longitude / REP_SCALE_FACTOR) +
              "}";
        }

        /**
         * Compares the specified object with this GeoPoint for equality.
         * @return    gp != null && (gp is GeoPoint)
         *         && gp.latitude == this.latitude && gp.longitude == this.longitude
         **/
        public bool equals(object o)
        {
            if (!(o is GeoPoint))
                return false;

            GeoPoint other = (GeoPoint)o;
            return
              (this.latitude == other.latitude) &&
              (this.longitude == other.longitude);
        }

        // specified by superclass (object)
        public int hashCode()
        {
            return latitude * 7 + longitude * 41;
        }

        /** Computes the distance between GeoPoints.
         * @requires gp != null
         * @return a close approximation of as-the-crow-flies distance, in
         *         miles, from this to gp
         **/
        public double distanceTo(GeoPoint gp)
        {
            Assert.assertNotNull(gp);

            double x = (gp.latitude - this.latitude) * 69.023 / REP_SCALE_FACTOR;
            double y = (gp.longitude - this.longitude) * 51.075 / REP_SCALE_FACTOR;
            return System.Math.Sqrt(x * x + y * y);
        }

        /** Computes the compass heading between GeoPoints.
         * @requires gp != null && !this.equals(gp)
         * @return a close approximation of compass heading h from this to
         *         gp, in degrees, such that 0 <= h < 360.  In compass
         *         headings, * north = 0, east = 90, south = 180, and west =
         *         270.
         **/
        public double headingTo(GeoPoint gp)
        {
            Assert.assertNotNull(gp);
            Assert.assertTrue(!equals(gp));

            double x = (gp.latitude - this.latitude) * 69.023 / REP_SCALE_FACTOR;
            double y = (gp.longitude - this.longitude) * 51.075 / REP_SCALE_FACTOR;

            double angle = (System.Math.Atan2(y, x)) * 180.0 / System.Math.PI;
            if (angle < 0)
            {
                angle += 360.0;
            }
            return angle;
        }

    } // GeoPoint
}