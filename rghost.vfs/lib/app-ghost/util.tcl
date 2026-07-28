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

proc AddSessionTotals {settings_name current_name baseline_name elapsed} {
    upvar 1 $settings_name settings
    upvar 1 $current_name current
    upvar 1 $baseline_name baseline

    incr settings(runtime) $elapsed
    foreach key {actual_down actual_up reported_down reported_up} {
        incr settings($key) [expr {$current($key) - $baseline($key)}]
        set baseline($key) $current($key)
    }
}

proc version_compare {v1 v2} {
    set l1 [normalize_version_components $v1]
    set l2 [normalize_version_components $v2]
    while {[llength $l1] < [llength $l2]} {lappend l1 0}
    while {[llength $l2] < [llength $l1]} {lappend l2 0}
    foreach c1 $l1 c2 $l2 {
        if {$c2 > $c1} {return 1}
        if {$c2 < $c1} {return 0}
    }
    return 0
}

proc normalize_version_components {version} {
    set version [string trimleft $version vV]
    set components {}
    foreach part [split $version .] {
        if {[regexp {^([0-9]+)} $part _ number]} {
            lappend components $number
        } else {
            lappend components 0
        }
    }
    if {[llength $components] == 0} {
        return {0}
    }
    return $components
}

proc GetTrustedCaFile {} {
    set candidates {}
    if {[info exists ::env(SSL_CERT_FILE)] && $::env(SSL_CERT_FILE) ne ""} {
        lappend candidates $::env(SSL_CERT_FILE)
    }
    if {[info exists ::rg_dir]} {
        lappend candidates [file join $::rg_dir tls cacert.pem]
    }
    foreach env_name {ProgramW6432 ProgramFiles} {
        if {[info exists ::env($env_name)]} {
            lappend candidates \
                [file join $::env($env_name) Git usr ssl certs ca-bundle.crt] \
                [file join $::env($env_name) Git mingw64 etc ssl certs ca-bundle.crt]
        }
    }
    if {[info exists ::env(SystemDrive)]} {
        lappend candidates \
            [file join $::env(SystemDrive)/ {Program Files} Git usr ssl certs ca-bundle.crt] \
            [file join $::env(SystemDrive)/ {Program Files} Git mingw64 etc ssl certs ca-bundle.crt]
    }
    foreach path {
        /etc/ssl/certs/ca-certificates.crt
        /etc/pki/tls/certs/ca-bundle.crt
        /etc/ssl/ca-bundle.pem
    } {
        lappend candidates $path
    }

    foreach path $candidates {
        if {[file isfile $path] && [file size $path] > 0} {
            return [file normalize $path]
        }
    }
    return -code error "No trusted CA bundle was found. Set SSL_CERT_FILE or install the packaged RatioGhost release."
}

proc TlsHostnameMatches {hostname certificate} {
    set hostname [string tolower [string trimright $hostname .]]
    if {$hostname eq ""} {return 0}

    set names {}
    if {[dict exists $certificate subjectAltName]} {
        foreach name [dict get $certificate subjectAltName] {
            regsub -nocase {^(DNS:|IP Address:)} $name {} name
            lappend names $name
        }
    }
    if {[llength $names] == 0 && [dict exists $certificate subject]} {
        set subject [dict get $certificate subject]
        if {[regexp -nocase {(?:^|,)CN=([^,]+)} $subject _ common_name]} {
            lappend names $common_name
        }
    }

    foreach name $names {
        set name [string tolower [string trimright [string trim $name] .]]
        if {$name eq $hostname} {return 1}
        if {[string match {*.?*} $name] && [string range $name 0 1] eq "*."} {
            set suffix [string range $name 1 end]
            if {[string match "*$suffix" $hostname]} {
                set prefix [string range $hostname 0 end-[string length $suffix]]
                if {$prefix ne "" && [string first . $prefix] < 0} {
                    return 1
                }
            }
        }
    }
    return 0
}

proc TlsVerifyPeer {expected_host option args} {
    if {$option ne "verify"} {return 1}
    lassign $args channel depth certificate chain_ok error_message
    if {!$chain_ok} {return 0}
    if {$depth != 0} {return 1}
    return [TlsHostnameMatches $expected_host $certificate]
}

proc TlsClientOptions {expected_host} {
    set ca_file [GetTrustedCaFile]
    return [list \
        -ssl2 0 -ssl3 0 -tls1 0 -tls1.1 0 -tls1.2 1 \
        -request 1 -require 1 -cafile $ca_file \
        -servername $expected_host \
        -command [list TlsVerifyPeer $expected_host]]
}

proc GenerateTlsCertificate {cert_path key_path} {
    set log_path [file join [file dirname $cert_path] certificate-generation.log]
    if {$::WINDOWS} {
        set script [file join [file dirname $cert_path] generate-certificate.ps1]
        set script_body {
param(
    [Parameter(Mandatory = $true)][string]$CertificatePath,
    [Parameter(Mandatory = $true)][string]$KeyPath
)

$ErrorActionPreference = 'Stop'
[IO.File]::WriteAllText((Join-Path (Split-Path -Parent $CertificatePath) 'certificate-generation-invoked.txt'), 'invoked')

function Convert-ToPem([string]$Label, [byte[]]$Bytes) {
    $base64 = [Convert]::ToBase64String($Bytes, [Base64FormattingOptions]::InsertLineBreaks)
    return "-----BEGIN $Label-----`r`n$base64`r`n-----END $Label-----`r`n"
}

$rsa = [Security.Cryptography.RSA]::Create(2048)
$certificate = $null
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=Ratio Ghost Local Proxy',
        $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1
    )
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true)
    )
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
            $true
        )
    )
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName('localhost')
    $san.AddIpAddress([Net.IPAddress]::Loopback)
    $request.CertificateExtensions.Add($san.Build())

    $certificate = $request.CreateSelfSigned(
        [DateTimeOffset]::UtcNow.AddMinutes(-5),
        [DateTimeOffset]::UtcNow.AddYears(5)
    )
    [IO.File]::WriteAllText($CertificatePath, (Convert-ToPem 'CERTIFICATE' $certificate.Export('Cert')))
    if ($null -ne $rsa.PSObject.Methods['ExportPkcs8PrivateKey']) {
        $privateKey = $rsa.ExportPkcs8PrivateKey()
    } elseif ($rsa -is [Security.Cryptography.RSACng]) {
        $privateKey = $rsa.Key.Export([Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    } else {
        throw 'This Windows installation cannot export a PKCS#8 private key.'
    }
    [IO.File]::WriteAllText($KeyPath, (Convert-ToPem 'PRIVATE KEY' $privateKey))
} finally {
    if ($null -ne $certificate) { $certificate.Dispose() }
    $rsa.Dispose()
}
}
        set log [open $log_path a]
        puts $log "[clock format [clock seconds]] writing inline certificate script to $script"
        close $log
        set fd [open $script w]
        fconfigure $fd -translation crlf -encoding utf-8
        puts -nonewline $fd $script_body
        close $fd
        set ps [file join $::env(WINDIR) System32 WindowsPowerShell v1.0 powershell.exe]
        set cmd [auto_execok cmd.exe]
        if {![file exists $ps] || $cmd eq ""} {
            return -code error "powershell.exe was not found"
        }
        try {
            set out_file [file join [file dirname $cert_path] certificate-generation.out]
            set err_file [file join [file dirname $cert_path] certificate-generation.err]
            catch {file delete -force $out_file $err_file}
            set qps [file nativename $ps]
            set qscript [file nativename $script]
            set qcert [file nativename $cert_path]
            set qkey [file nativename $key_path]
            set qout [file nativename $out_file]
            set qerr [file nativename $err_file]
            set cmd_script [file join [file dirname $cert_path] run-certificate-generator.cmd]
            set command_line "\"$qps\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"$qscript\" -CertificatePath \"$qcert\" -KeyPath \"$qkey\" > \"$qout\" 2> \"$qerr\""
            set fd [open $cmd_script w]
            fconfigure $fd -translation crlf -encoding ascii
            puts $fd "@echo off"
            puts $fd $command_line
            close $fd
            set code [catch {
                exec {*}$cmd /c [file nativename $cmd_script]
            } output options]
            set ps_output ""
            set ps_error ""
            if {[file exists $out_file]} {
                set fd [open $out_file r]
                set ps_output [read $fd]
                close $fd
            }
            if {[file exists $err_file]} {
                set fd [open $err_file r]
                set ps_error [read $fd]
                close $fd
            }
            set log [open $log_path a]
            puts $log "powershell=$ps script_size=[file size $script]"
            puts $log "command=$command_line"
            puts $log "exit=$code output=$output stdout=$ps_output stderr=$ps_error options=$options"
            puts $log "after-exec cert=[file exists $cert_path] key=[file exists $key_path] cert_path=$cert_path key_path=$key_path"
            close $log
            if {$code} {
                return -code error "certificate generator failed: $output; see $log_path"
            }
        } finally {
            if {[file exists $cert_path] && [file exists $key_path]} {
                catch {file delete -force $script}
            }
        }
    } else {
        exec openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 1825 \
            -subj /CN=RatioGhost-Local-Proxy -keyout $key_path -out $cert_path
    }

    if {![file exists $cert_path] || ![file exists $key_path]} {
        return -code error "certificate generator did not create the expected files; see $log_path"
    }
    catch {file delete -force $script $cmd_script $out_file $err_file [file join [file dirname $cert_path] certificate-generation-invoked.txt]}
    catch {file attributes $key_path -permissions 0600}
}


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
        if {$num >= $n} {
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
        if {$num >= $n} {
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
