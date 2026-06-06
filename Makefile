.PHONY: publish publish-keep-iis test clean

publish:
	dotnet run scripts/publish.cs

publish-keep-iis:
	dotnet run scripts/publish.cs --keep-iis-artifacts

test:
	dotnet test

clean:
	rm -rf dist/
