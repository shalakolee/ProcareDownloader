using System.Reflection;
using System.Text.Json.Nodes;
using ProcareDownloader.Models;
using ProcareDownloader.Services;

namespace ProcareDownloader.Tests;

public sealed class ProcarePayloadParserTests
{
    [Fact]
    public void CoreParser_ReadsStudentsFromNestedDataArray()
    {
        var json = JsonNode.Parse("""
        {
          "data": [
            {
              "id": "kid-1",
              "attributes": {
                "first_name": "Avery",
                "last_name": "Lee",
                "photo_url": "https://example.test/avatar.jpg"
              }
            }
          ]
        }
        """);

        var students = InvokePrivateParser<List<Student>>("ParseStudents", json!, "test students");

        var student = Assert.Single(students);
        Assert.Equal("kid-1", student.Id);
        Assert.Equal("Avery", student.FirstName);
        Assert.Equal("Lee", student.LastName);
        Assert.Equal("https://example.test/avatar.jpg", student.PhotoUrl);
    }

    [Fact]
    public void CoreParser_ReadsStudentPhotoFromNestedAvatarImage()
    {
        var json = JsonNode.Parse("""
        {
          "data": [
            {
              "id": "kid-2",
              "attributes": {
                "firstName": "Noah",
                "lastName": "Lee",
                "avatar": {
                  "small": "https://example.test/noah-avatar.jpg"
                }
              }
            }
          ]
        }
        """);

        var students = InvokePrivateParser<List<Student>>("ParseStudents", json!, "test nested avatar");

        var student = Assert.Single(students);
        Assert.Equal("kid-2", student.Id);
        Assert.Equal("https://example.test/noah-avatar.jpg", student.PhotoUrl);
    }

    [Fact]
    public void CoreParser_ReadsPhotosAndStudentIdsFromActivityPayload()
    {
        var json = JsonNode.Parse("""
        {
          "data": [
            {
              "id": "activity-1",
              "attributes": {
                "created_at": "2026-04-12T10:30:00Z",
                "caption": "Playground",
                "kid_ids": ["kid-1"],
                "media": [
                  {
                    "id": "photo-1",
                    "thumbnail_url": "https://example.test/thumb.jpg",
                    "original_url": "https://example.test/full.jpg"
                  }
                ]
              }
            }
          ]
        }
        """);

        var photos = InvokePrivateParser<List<Photo>>("ParsePhotos", json!, "test photos");

        var photo = Assert.Single(photos);
        Assert.Equal("activity-1", photo.Id);
        Assert.Equal("https://example.test/thumb.jpg", photo.ThumbnailUrl);
        Assert.Equal("https://example.test/full.jpg", photo.OriginalUrl);
        Assert.Equal("Playground", photo.Caption);
        Assert.Equal(["kid-1"], photo.StudentIds);
    }

    private static T InvokePrivateParser<T>(string methodName, JsonNode node, string context)
    {
        var method = typeof(ProcareApiClient).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<T>(method.Invoke(null, [node, context]));
    }
}
