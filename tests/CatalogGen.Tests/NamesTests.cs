using CatalogGen;

namespace CatalogGen.Tests;

public class NamesTests
{
    [Theory]
    [InlineData("Assassin's Creed™ II", "assassinscreedii")]
    [InlineData("Assassin’s Creed® II", "assassinscreedii")]
    [InlineData("ABZÛ", "abzu")]
    [InlineData("Pokémon: Legends", "pokemonlegends")]
    [InlineData("  Half-Life 2  ", "halflife2")]
    [InlineData("™®", "")]
    public void Normalize_keeps_lowercase_letters_and_digits_only(string name, string expected) =>
        Assert.Equal(expected, Names.Normalize(name));

    [Theory]
    [InlineData("Assassin’s Creed� II", "Assassin’s Creed II")]
    [InlineData("Assassin�s Creed", "Assassin’s Creed")]
    [InlineData("Batman™:  Arkham   Knight", "Batman: Arkham Knight")]
    public void Clean_removes_trademark_signs_and_broken_encoding(string name, string expected) =>
        Assert.Equal(expected, Names.Clean(name));

    [Theory]
    [InlineData("Alien: Isolation", "alien-isolation")]
    [InlineData("Death's Door", "death-s-door")]
    [InlineData("  ABZÛ!", "abzu")]
    public void Slug_is_lowercase_words_joined_by_dashes(string name, string expected) =>
        Assert.Equal(expected, Names.Slug(name));
}
