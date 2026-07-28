package require tcltest
namespace import ::tcltest::*

configure -testdir [file dirname [info script]] -singleproc 1
runAllTests

if {$::tcltest::numTests(Failed) > 0} {
    exit 1
}
