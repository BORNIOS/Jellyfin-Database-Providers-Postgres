using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Jellyfin.Database.Providers.Postgres.Services;
using Jellyfin.Plugin.JellyTrend.Api;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Fija la parte del contrato con JellyTrend que el compilador no puede comprobar.
/// </summary>
/// <remarks>
/// <para>
/// Este plugin compila su propia copia del contrato (<c>JellyTrendContract/</c>) y JellyTrend lo encuentra
/// buscando esa copia entre los ensamblados cargados, enlazando cada llamada por nombre. Es decir: los
/// nombres SON el contrato. Renombrar un metodo, cambiar un parametro o tocar las propiedades de la
/// proyeccion rompe la integracion sin error de compilacion en ninguno de los dos lados.
/// </para>
/// <para>
/// Estas pruebas, junto con su gemela en el repo de JellyTrend, obligan a que las dos versiones avancen
/// alineadas. La dependencia sigue siendo opcional en tiempo de ejecucion: sin JellyTrend instalado no se
/// pierde ninguna funcionalidad de este plugin, y sin este plugin JellyTrend usa ILibraryManager.
/// </para>
/// </remarks>
public sealed class JellyTrendContractTests
{
    /// <summary>JellyTrend reconoce esta copia por su nombre completo, no por el nombre del plugin.</summary>
    [Fact]
    public void ElEspejoConservaElNombreCompletoDelContrato()
    {
        Assert.Equal("Jellyfin.Plugin.JellyTrend.Api.IRecommendationQueryProvider", typeof(IRecommendationQueryProvider).FullName);
        Assert.Equal("Jellyfin.Plugin.JellyTrend.Api.RecommendationItem", typeof(RecommendationItem).FullName);
    }

    [Fact]
    public void ElEspejoTieneLaMismaFormaQueElContratoDeJellyTrend()
    {
        Assert.Equal(
            [
                "GetPlayedMovies(Guid, Int32) -> IReadOnlyList<RecommendationItem>",
                "GetRandomUnwatchedMovies(Guid, IReadOnlyList<Guid>, Int32) -> IReadOnlyList<RecommendationItem>",
                "GetResumableMovies(Guid, Int32) -> IReadOnlyList<RecommendationItem>",
                "GetUnwatchedMoviesByGenres(Guid, IReadOnlyList<String>, IReadOnlyList<Guid>, Int32) -> IReadOnlyList<RecommendationItem>",
                "GetUnwatchedMoviesByPersons(Guid, IReadOnlyList<Guid>, IReadOnlyList<Guid>, Int32) -> IReadOnlyList<RecommendationItem>",
                "GetUnwatchedMoviesByTags(Guid, IReadOnlyList<String>, IReadOnlyList<Guid>, Int32) -> IReadOnlyList<RecommendationItem>"
            ],
            typeof(IRecommendationQueryProvider).GetMethods().Select(Describe).OrderBy(static text => text, StringComparer.Ordinal));
    }

    /// <summary>
    /// La busqueda de JellyTrend lee la proyeccion por nombre de propiedad: si cambian, deja de entenderla.
    /// </summary>
    [Fact]
    public void LaProyeccionTieneLasMismasPropiedades()
    {
        Assert.Equal(
            [
                "CommunityRating: Nullable<Single>",
                "Genres: IReadOnlyList<String>",
                "Id: Guid",
                "PremiereDate: Nullable<DateTime>",
                "Studios: IReadOnlyList<String>",
                "Tags: IReadOnlyList<String>",
                "TmdbId: String"
            ],
            typeof(RecommendationItem).GetProperties().Select(static property => $"{property.Name}: {Friendly(property.PropertyType)}").OrderBy(static text => text, StringComparer.Ordinal));
    }

    /// <summary>
    /// JellyTrend construye la implementacion por reflexion: necesita un tipo publico y un constructor sin
    /// parametros o con un unico <see cref="ILogger{T}"/>, como el resto de servicios de Jellyfin.
    /// </summary>
    [Fact]
    public void JellyTrendPuedeConstruirLaImplementacion()
    {
        var implementation = typeof(PostgresRecommendationQueryProvider);

        Assert.True(implementation.IsPublic, "El tipo debe ser publico para que se pueda encontrar.");
        Assert.False(implementation.IsAbstract);

        var constructible = implementation.GetConstructors().Any(static constructor =>
            constructor.GetParameters().Length == 0
            || (constructor.GetParameters() is [{ ParameterType: var parameter }]
                && parameter.IsGenericType
                && parameter.GetGenericTypeDefinition() == typeof(ILogger<>)));

        Assert.True(constructible, "JellyTrend solo puede construir tipos sin parametros o con un unico ILogger<T>.");
    }

    private static string Describe(MethodInfo method)
        => $"{method.Name}({string.Join(", ", method.GetParameters().Select(static parameter => Friendly(parameter.ParameterType)))}) -> {Friendly(method.ReturnType)}";

    private static string Friendly(Type type)
    {
        if (type.IsGenericType && type.Name.Contains('`', StringComparison.Ordinal))
        {
            var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Friendly))}>";
        }

        return type.Name;
    }
}
