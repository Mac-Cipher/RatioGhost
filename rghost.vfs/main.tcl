#   Ratio Ghost - BitTorrent ratio modifying proxy
#   Copyright (C) 2006-2015 Yasmine@RatioGhost.com
#
#   This program is free software: you can redistribute it and/or modify
#   it under the terms of the GNU General Public License as published by
#   the Free Software Foundation, either version 3 of the License, or
#   (at your option) any later version.
#
#   This program is distributed in the hope that it will be useful,
#   but WITHOUT ANY WARRANTY; without even the implied warranty of
#   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
#   GNU General Public License for more details.
#
#   You should have received a copy of the GNU General Public License
#   along with this program.  If not, see <http://www.gnu.org/licenses/>.

# Initialize debug log
catch {
    set fd [open "C:/Users/LUCAS/AppData/Roaming/RatioGhost/debug_app_err.txt" w]
    puts $fd "main.tcl started"
    close $fd
}

proc dlog_main {msg} {
    catch {
        set fd [open "C:/Users/LUCAS/AppData/Roaming/RatioGhost/debug_app_err.txt" a]
        puts $fd $msg
        close $fd
    }
}

dlog_main "Setting auto_path..."
set auto_path [linsert $auto_path 0 ./rghost.vfs/lib]

dlog_main "Checking starkit..."
catch {
    package require starkit
    if {[starkit::startup] eq "sourced"} return
}

if {[info exists ::starkit::topdir]} {
    set ::rg_dir $::starkit::topdir
} else {
    set ::rg_dir [file normalize [file dirname [info script]]]
}

dlog_main "rg_dir is $::rg_dir"

set WINDOWS [string match Windows* $tcl_platform(os)]
set LINUX [string match Linux* $tcl_platform(os)]
if {!$WINDOWS && !$LINUX} {set MAC 1} else {set MAC 0}

dlog_main "Platform flags: WINDOWS=$WINDOWS, LINUX=$LINUX, MAC=$MAC"

if {$::WINDOWS} {
    dlog_main "Loading dde..."
    if {[catch {package require dde} err]} {
        dlog_main "DDE package require failed: $err"
    }

    set topicName RatioGhost2015
    dlog_main "Checking DDE services..."

    if {[catch {
        set otherServices [dde services TclEval $topicName]
        dlog_main "Other services found: [llength $otherServices]"
        if {[llength $otherServices] > 0} {
            dlog_main "Bringing other service to front and exiting..."
            dde execute TclEval $topicName {
                wm deiconify .
                raise .
                bell
            }
            exit
        }
    } err]} {
        dlog_main "DDE check failed: $err"
    }

    if {[catch {
        dde servername $topicName
        dlog_main "Registered DDE servername $topicName"
    } err]} {
        dlog_main "DDE servername registration failed: $err"
    }

    if {![info exists env(APPDATA)]} {
        tk_messageBox -icon error -title "Ratio Ghost" -message "Sorry, your version of Windows is not supported. Please consider upgrading."
        exit
    }
}

dlog_main "Requiring package app-ghost..."
if {[catch {
    package require app-ghost
    dlog_main "Package app-ghost loaded successfully!"
} err]} {
    dlog_main "ERROR loading package app-ghost: $err"
    dlog_main "errorInfo:\n$::errorInfo"
}
