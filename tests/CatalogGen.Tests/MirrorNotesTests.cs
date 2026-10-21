using CatalogGen;

namespace CatalogGen.Tests;

public class MirrorNotesTests
{
    private const string Mit = """
        MIT License

        Copyright (c) 2024 Jane Doe

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software ...
        """;

    [Fact]
    public void One_copyright_line_is_found()
    {
        Assert.Equal(["Copyright (c) 2024 Jane Doe"], MirrorNotes.CopyrightLines(Mit));
    }

    [Fact]
    public void Two_lines_are_kept_in_order_trimmed_and_without_duplicates()
    {
        var text = "MIT License\r\n\r\n  Copyright (c) 2023 A  \r\ncopyright (c) 2024 B\r\nCopyright (c) 2023 A\r\nPermission is hereby granted";

        Assert.Equal(["Copyright (c) 2023 A", "copyright (c) 2024 B"], MirrorNotes.CopyrightLines(text));
    }

    [Fact]
    public void Apache_style_lines_count_but_the_appendix_template_does_not()
    {
        var text = "Apache License\nCopyright 2020 Example Corp\n...\nAPPENDIX\n   Copyright [yyyy] [name of copyright owner]\n";

        Assert.Equal(["Copyright 2020 Example Corp"], MirrorNotes.CopyrightLines(text));
    }

    [Theory]
    [InlineData("The Unlicense\nThis is free and unencumbered software released into the public domain.")]
    [InlineData("")]
    [InlineData(null)]
    public void No_line_gives_an_empty_list(string? text) => Assert.Empty(MirrorNotes.CopyrightLines(text));

    private static readonly RepoNotes B = new("zeta/b-repo", "Apache-2.0", "https://github.com/zeta/b-repo/blob/main/LICENSE", ["Copyright 2020 Z"], ["z2.addon64", "z1.addon64"]);
    private static readonly RepoNotes A = new("Alpha/a-repo", "MIT", "https://github.com/Alpha/a-repo/blob/main/LICENSE", ["Copyright (c) 1", "Copyright (c) 2"], ["a.addon64"]);

    [Fact]
    public void The_body_has_the_intro_then_one_block_per_repository_sorted_with_sorted_files()
    {
        var body = MirrorNotes.Build([B, A]);

        Assert.Equal(MirrorNotes.Intro + "\n\n" + """
            ### Alpha/a-repo
            - Repository: https://github.com/Alpha/a-repo
            - Licence: MIT (https://github.com/Alpha/a-repo/blob/main/LICENSE)
            - Copyright:
              - Copyright (c) 1
              - Copyright (c) 2
            - Files:
              - a.addon64

            ### zeta/b-repo
            - Repository: https://github.com/zeta/b-repo
            - Licence: Apache-2.0 (https://github.com/zeta/b-repo/blob/main/LICENSE)
            - Copyright:
              - Copyright 2020 Z
            - Files:
              - z1.addon64
              - z2.addon64
            """.Replace("\r\n", "\n"), body);
    }

    [Fact]
    public void The_same_input_in_any_order_gives_the_same_body()
    {
        Assert.Equal(MirrorNotes.Build([A, B]), MirrorNotes.Build([B, A]));
        Assert.Equal(MirrorNotes.Build([A, B]), MirrorNotes.Build([A, B]));
    }

    [Fact]
    public void Without_a_copyright_line_the_body_points_to_the_licence_file()
    {
        var body = MirrorNotes.Build([new RepoNotes("o/r", "Unlicense", "https://github.com/o/r/blob/main/UNLICENSE", [], ["x.addon64"])]);

        Assert.Contains("- Copyright holder: see the licence file at https://github.com/o/r/blob/main/UNLICENSE", body);
        Assert.DoesNotContain("- Copyright:", body);
    }

    [Fact]
    public void The_existing_intro_paragraph_stays_and_the_rest_is_replaced()
    {
        var existing = "My own intro.\r\n\r\n### old/repo\r\n- Files:\r\n  - old";
        var body = MirrorNotes.Build([A]);

        var merged = MirrorNotes.WithIntroOf(existing, body);

        Assert.StartsWith("My own intro.\n\n### Alpha/a-repo", merged);
        Assert.DoesNotContain("old/repo", merged);
        Assert.Equal(merged, MirrorNotes.WithIntroOf(merged, body));
    }

    [Fact]
    public void An_empty_existing_body_takes_the_intro_of_the_new_one()
    {
        var body = MirrorNotes.Build([A]);

        Assert.Equal(body, MirrorNotes.WithIntroOf(null, body));
        Assert.Equal(MirrorNotes.Intro, MirrorNotes.WithIntroOf("", MirrorNotes.Build([])));
    }
}
