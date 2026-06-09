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

package provide app-util 1.0


proc OpenDocument {filename} {
    if {[catch {
        if {$::WINDOWS} {
            exec rundll32.exe url.dll,FileProtocolHandler $filename &
        } elseif {$::LINUX} {
            exec xdg-open $filename &
        } elseif {$::MAC} {
            exec open $filename &
        }
    }]} {
        tk_messageBox -icon error -title "File Open Error" -message "Error opening $filename."
    }
}


proc GetProfileDirectory {} {
    global env

    if {[file exists settings.dat]} {return [pwd]}

    if {$::WINDOWS} {
        set parent $env(APPDATA)
        set path [file join $parent RatioGhost]
    } elseif {$::MAC} {
        set path [file join $env(HOME) Library "Application Support" RatioGhost]
    } elseif {$::LINUX} {
        if {[info exists env(XDG_CONFIG_HOME)]} {
            set path [file join $env(XDG_CONFIG_HOME) RatioGhost]
        } else {
            set path [file join $env(HOME) .config RatioGhost]
        }
    } else {
        return [pwd]
    }

    if {![file isdirectory $path]} {file mkdir $path}

    return $path
}


proc ValidateReal {num} {
    return [regexp -- {^[0-9]{0,3}(\.[0-9]{0,3})?$} $num]
}


proc ValidatePer {num} {
    if {$num eq ""} {return 1}
    if {![regexp -- {^1?[0-9]{0,2}$} $num]} {return 0}
    return [expr {$num <= 100}]
}

proc ValidatePort {num} {
    if {$num eq ""} {return 1}
    if {![regexp -- {^[0-9]{0,5}$} $num]} {return 0}
    return [expr {$num <= 65534}]
}


proc FormatData {num} {
    set post {B}

    if {$num == 0} {return 0}

    foreach n {1099511627776 1073741824 1048576 1024} p {TB GB MB KB} {
        if {$num > $n} {
            set num [expr {1.0 * $num / $n}]
            set post $p
            break
        }
    }

    if {$post ne "B"} {
        set num [format %0.1f $num]
    } else {
        set num [expr {round($num)}]
    }
    return "$num$post"
}


proc FormatElapsed {num} {
    set post {s}

    if {$num == 0} {return 0}

    foreach n {86400 3600 60} p {d h m} {
        if {$num > $n} {
            set num [expr {1.0 * $num / $n}]
            set post $p
            break
        }
    }

    if {$post ne "s"} {
        set num [format %0.1f $num]
    } else {
        set num [expr {round($num)}]
    }
    return "$num$post"
}
