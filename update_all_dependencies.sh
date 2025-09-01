#!/bin/bash

# Update the codegen
echo "Updating NetDaemon.HassModel.CodeGen tool..."
dotnet tool update -g NetDaemon.HassModel.CodeGen

# Find all .csproj files and update their package references
find . -name "*.csproj" -print0 | while IFS= read -r -d $'\0' file; do
    echo "Processing project file: $file"

    # Extract package names from the project file
    packages=$(grep -oP '(?<=PackageReference Include=")[^"]*' "$file" | sort -u)

    if [ -z "$packages" ]; then
        echo "  No packages found in $file."
        continue
    fi

    # Loop through each package and update it to the latest version
    for package in $packages; do
        echo "  Updating package: $package"
        dotnet add "$file" package "$package"
    done
done

echo "All dependencies have been updated."
