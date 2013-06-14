# Makefile.common contains the targets for running an individual test.
# This Makefile is a driver for running all the tests.

# HOW TO ADD A FILE TO THE REGRESSION TESTSUITE:
# 1. Run make-test.pl $TEST_NAME
# 2. Edit $TEST_NAME/$TEST_NAME.cs and edit the Makefile
#    as necessary
# 3. The test suite can be run with "make". When the results
#    are correct, you can update the goals with "make update-goals"
#
# In this file:
# 4. Put the test into one of the existing categories, or make a new
#    category to contain it.
#

default: summary

summary: default-projects__diffs
	$(MAKE) summary-only

summary-w-daikon: default-projects__diffs-w-daikon
	$(MAKE) summary-only-w-daikon

working: default-projects__diffs-if-working
	$(MAKE) working-summary-only

working-w-daikon: default-projects__diffs-w-daikon-if-working
	$(MAKE) working-summary-only-w-daikon

summary-only: default-projects__summary__quietly

summary-only-w-daikon: default-projects__summary-w-daikon__quietly

working-summary-only: default-projects__summary-if-working__quietly

working-summary-only-w-daikon: default-projects__summary-w-daikon-if-working__quietly

# This target simulates what the nightly build-test does, but note that
# it actually makes the two targets separately (so that it can skip the
# second if Daikon didn't build).
nightly: nightly-summary nightly-summary-w-daikon

nightly-summary: nightly-tests__diffs-if-working
	$(MAKE) nightly-summary-only

nightly-summary-w-daikon: nightly-tests__diffs-w-daikon-if-working
	$(MAKE) summary-only-w-daikon

nightly-summary-only: nightly-tests__summary-if-working__quietly

nightly-summary-only-w-daikon: nightly-tests__summary-w-daikon-if-working__quietly

clean diffs invs update-goals:
	$(MAKE) default-projects__$@

# nightly-tests__%: default-projects__% medium-tests__%
nightly-tests__%: default-projects__% medium-easy-tests__%
	@

update-all:
	$(MAKE) nightly-tests__update-goals

clean-all:
	$(MAKE) nightly-tests__clean

#default-projects__%: 
default-projects__%: do-HelloWorld__% do-StackAr__% do-PolyCalc__% do-GeoPoint__% do-ExceptionTest__% do-MultipleThreads__% do-MultipleExits__% do-Generics__% do-AdvancedCollections__% do-LinkedList__% do-FSharp__% do-WpfBasic__% do-Enums__% do-PureMethods__% do-Shadowing__% do-VisualBasic__%
	@

# 'make do-SomeDiectory_target'
# e.g. 'make do-bzip2__clean'
do-%__quietly:
	@$(MAKE) --no-print-directory -C `echo $* | perl -pe 's/(.+?)__/$$1 /;'`

do-%:
	@$(MAKE) -C `echo $* | perl -pe 's/(.+?)__/$$1 /;'`
