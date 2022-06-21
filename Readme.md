# Neo4J BC Government Graph Import
This is a neo4j database import routine for bringing in BC Government Github.

It is quick and dirty utilizing octokit for consuming the github api and then string based cypher methods to insert data.

```docker run --publish=7474:7474 --publish=7687:7687 --volume=$HOME/neo4j/data:/data neo4j```

