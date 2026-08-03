# 👻 Ratio Ghost

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/UI-Avalonia-8B5CF6.svg)](https://avaloniaui.net/)
[![License: GPL v3](https://img.shields.io/badge/License-GPL%20v3-green.svg)](license.txt)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)](#)

Traductions : [🇬🇧 English](readme.md) | [🇫🇷 Français](README.fr.md)

Ratio Ghost est un proxy local HTTP/HTTPS qui modifie les annonces envoyées par votre client BitTorrent aux trackers privés.

L’application est écrite en C#/.NET 10 et utilise Avalonia pour son interface desktop. Le proxy reste limité à `localhost` et intercepte uniquement le trafic des trackers.

## Fonctionnalités

- Réécriture configurable des compteurs d’upload et de download.
- Mode FreeLeech et option Pretend to Seed.
- Seuil de leechers, multiplicateurs, boost d’upload et pause de réécriture.
- Proxy HTTP et interception HTTPS avec consentement explicite.
- CA locale propre à l’installation et certificats par hôte.
- Interface Avalonia, journal d’activité, tray Windows et démarrage automatique.
- Journal de diagnostic optionnel avec masquage des identifiants de trackers.

## Télécharger pour Windows

Les releases publiées contiennent l’archive autonome .NET :

1. Ouvrez la page des [Releases](https://github.com/Mac-Cipher/RatioGhost/releases).
2. Téléchargez `RatioGhost-dotnet-win-x64.zip` et son fichier `.sha256`.
3. Vérifiez le checksum avant de décompresser l’archive :

   ```powershell
   Get-FileHash .\RatioGhost-dotnet-win-x64.zip -Algorithm SHA256
   Get-Content .\RatioGhost-dotnet-win-x64.zip.sha256
   ```

4. Décompressez l’archive puis lancez `RatioGhost.exe`.

L’activation HTTPS demande une confirmation dans l’onglet **Platform**, puis dans le dialogue de sécurité Windows. Le package officiellement distribué est `win-x64`.

## Lancer depuis le code source

Prérequis : .NET SDK `10.0.302`.

```powershell
dotnet restore .\RatioGhost.slnx
dotnet run --project .\src\RatioGhost.Desktop\RatioGhost.Desktop.csproj
```

Pour rediriger les annonces du client torrent :

1. Configurez un proxy HTTP sur `127.0.0.1:3773`.
2. Activez la résolution des noms d’hôtes par le proxy.
3. N’utilisez pas Ratio Ghost comme proxy pour les connexions pair-à-pair.

Pour qBittorrent, activez le proxy pour les communications avec les trackers et laissez les connexions pair-à-pair désactivées.

Ratio Ghost ne masque pas le trafic pair-à-pair et ne modifie pas les connexions entre les pairs.

## Configuration et migration des paramètres

La configuration courante est stockée en JSON dans le profil Ratio Ghost. Si `settings.json` n’existe pas encore, l’application peut importer un ancien `settings.dat` comme données uniquement, sans exécuter de code, puis écrire la configuration JSON. Les fichiers d’origine ne sont pas modifiés.

## Développement et vérification

```powershell
dotnet test .\tests-dotnet\RatioGhost.Core.Tests\RatioGhost.Core.Tests.csproj -c Release
dotnet test .\tests-dotnet\RatioGhost.Proxy.Tests\RatioGhost.Proxy.Tests.csproj -c Release
dotnet test .\tests-dotnet\RatioGhost.Desktop.Tests\RatioGhost.Desktop.Tests.csproj -c Release
dotnet build .\RatioGhost.slnx -c Release
.\scripts\package-win-x64.ps1
.\scripts\smoke-win-x64.ps1
```

Le test de confiance Windows est volontairement opt-in et affiche un dialogue de sécurité. Ne le lancez qu’avec un consentement explicite.

## Architecture

- `src/RatioGhost.Core` : configuration, parsing et transformation des annonces.
- `src/RatioGhost.Proxy` : proxy HTTP/HTTPS asynchrone, limites réseau et journalisation redacted.
- `src/RatioGhost.Desktop` : interface Avalonia, tray Windows, certificats et autostart.
- `tests-dotnet` : tests unitaires, intégration réseau et tests de packaging.
- `scripts` : publication, packaging et smoke test Windows.
- `assets` : ressources de l’application.

Le dépôt ne contient qu’une implémentation applicative .NET/Avalonia. Les builds Linux et macOS valident les frontières de plateforme et la compilation ; la distribution desktop officielle reste Windows.

## Licence

Distribué sous licence GNU General Public License v3. Voir [`license.txt`](license.txt).
