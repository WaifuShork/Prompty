namespace Prompty;

public static class Templates
{
    public static readonly IReadOnlyList<(string Name, string Body)> All =
    [
        ("Structured task", """
            ## Task


            ## Context


            ## Constraints


            ## Acceptance criteria


            ## Output format

            """),
        ("Bug fix", """
            ## Problem


            ## Observed behavior


            ## Expected behavior


            ## Relevant files


            ## Constraints

            """),
        ("Feature", """
            ## Goal


            ## Requirements


            ## Out of scope


            ## Context


            ## Done when

            """),
        ("Code review", """
            ## What to review


            ## Focus areas


            ## Context


            ## Output format

            """),
        ("XML tags", """
            <task>
            </task>

            <context>
            </context>

            <instructions>
            </instructions>

            <output_format>
            </output_format>

            """),
        ("Blank", ""),
    ];
}
