; Unshipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------------------------
FGQL014 | Feather.GraphQL | Error | Selecting an object member that has no scalar fields of its own
FGQL012 | Feather.GraphQL | Warning | A query with no Where, Take or Select asks for every record
FGQL015 | Feather.GraphQL | Warning | A chain marked [GraphQLQuery] that could not be compiled
FGQL016 | Feather.GraphQL | Error | A declared query whose reply cannot be modelled
