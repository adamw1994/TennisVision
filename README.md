# TennisVision

Mała aplikacja konsolowa .NET 10 do analizy nagrań tenisa.

Uruchomienie z katalogu projektu:

```powershell
dotnet run -- "C:\TennisSamples\Wojtek1set-2min2.mp4"
```

Każde uruchomienie tworzy nowy folder:

```text
output/runs/<nazwa-filmu>-<czas-UTC>-<unikalny-identyfikator>/
  impacts.csv
  rallies.csv
  <nazwa-filmu>-highlights.mp4
```

`impacts.csv` zawiera wykryte czasy uderzeń i parametry dźwięku.
`rallies.csv` łączy kolejne detekcje w kandydatów na akcje. MP4 zawiera te akcje
z ustawionym zapasem czasu. Przy braku detekcji powstają dwa CSV bez pustego MP4.
Filmy źródłowe pozostają w `C:\TennisSamples`; wyniki trafiają do projektu.

## Układ projektu

```text
src/
  Program.cs                    Główna ścieżka: audio -> rallies -> MP4.
  Configuration/                Odczyt i walidacja ustawień.
  Detection/                    Grupowanie uderzeń i detekcja osób.
  Commands/                     Polecenia detect-players i analyze-rallies.
  Video/                        Pobieranie klatek i eksport MP4.
docs/                           Instrukcje poszczególnych poleceń.
data/reference/                 Ręcznie oznaczone uderzenia do porównywania wyników.
models/                         Pobrane modele ONNX.
output/                         Wyniki kolejnych uruchomień i porównań modeli.
appsettings.json                Parametry aplikacji.
TennisVision.csproj             Jeden projekt .NET 10.
```

Pliki `.cs` z `src/` są automatycznie kompilowane przez istniejący projekt .NET.
Katalogi `bin/` i `obj/` zawierają pliki generowane przez narzędzia .NET.
Parametr `VideoExport.OutputDirectory` ustawia katalog nowych wyników;
pozostałe parametry detekcji zachowują dotychczasowe wartości.

## Instrukcje

- [Wideo wejściowe -> MP4 z akcjami](docs/VIDEO_EXPORT.md)
- [Wykrywanie osób na klatkach](docs/PLAYER_DETECTION.md)
- [Zbiorczy raport widoczności osób](docs/RALLY_ANALYSIS.md)
- [Oznaczenia referencyjne](data/reference/README.md)

Istniejące polecenia `detect-players` i `analyze-rallies` przyjmują `rallies.csv`
z wybranego folderu wyników. Nie należy mieszać CSV z różnych filmów.

Poprzednie CSV i MP4 z highlights z `C:\TennisSamples` przeniesiono do
`C:\TennisSamples\todelete\previous-results-20261107`. To wygenerowane wyniki,
pozostawione do ręcznego przejrzenia. Filmy źródłowe nie zostały przeniesione.
